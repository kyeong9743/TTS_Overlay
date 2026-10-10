import os
import io
import yaml
import torch
import librosa
import numpy as np
from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field
import scipy.io.wavfile as wavf
from nltk.tokenize import word_tokenize
import nltk
nltk.download('punkt')
nltk.download('punkt_tab')
import phonemizer

# StyleTTS2 관련 import
from models import build_model_no_bert
from utils import recursive_munch
from text_utils import TextCleaner
from Modules.diffusion.sampler import DiffusionSampler, ADPM2Sampler, KarrasSchedule

app = FastAPI(title="StyleTTS2 Korean API")

MAX_TEXT_CHARS = 2000            # 한 번에 합성할 수 있는 최대 글자 수
MAX_REF_B64_CHARS = 70_000_000   # 참조 오디오 업로드 상한 (base64 기준, 원본 약 50MB)

class TTSRequest(BaseModel):
    # 아래 범위 제한은 잘못된 값 하나로 GPU 메모리가 고갈되거나 서버가 장시간
    # 점유되는 것을 막기 위한 상한임. 범위를 벗어나면 FastAPI가 422로 거부함.
    text: str = Field(max_length=MAX_TEXT_CHARS)
    # 서버측 경로. 컨테이너 "내부"에 실제로 있는 파일에만 유효.
    speaker_id: str = "/app/Outputs/libritts_vocos_stage2/100_121669_000031_000001_0.wav"
    # 권장 방식: 클라이언트가 사용자 PC의 로컬 wav를 읽어 base64로 업로드.
    # 컨테이너는 사용자 파일시스템을 볼 수 없으므로, speaker_id에 담긴 윈도우 경로는
    # 여기서 절대 해석되지 않음.
    speaker_wav_b64: str | None = Field(default=None, max_length=MAX_REF_B64_CHARS)
    # 음성 복제 튜닝값. alpha/beta가 낮을수록 참조 화자의 음색, 운율에 가까워짐(복제 강도가 높아짐).
    # 높을수록 표현력은 늘지만 유사도는 떨어짐.
    # diffusion_steps는 속도와 품질(노이즈)을 맞바꾸는 값.
    alpha: float = Field(default=0.1, ge=0.0, le=1.0)
    beta: float = Field(default=0.5, ge=0.0, le=1.0)
    diffusion_steps: int = Field(default=10, ge=1, le=100)
    embedding_scale: float = Field(default=1.0, ge=0.0, le=10.0)
    # 발화 속도. >1이면 빠르게, <1이면 느리게. 예측된 음소 길이를 스케일함
    # (1.2 = 약 20% 빠름).
    speed: float = Field(default=1.0, ge=0.1, le=4.0)
    # 참조 오디오에서 사용할 구간(초). 업로드된 wav가 MAX_REF_SEC보다 길 때
    # 어느 부분을 복제에 쓸지 호출측이 고르도록 하는 용도.
    ref_start: float | None = None
    ref_end: float | None = None

# 전역 변수
model = None
config = None
sampler = None
load_report = {"loaded": [], "remapped": [], "missing": [], "error": None}
textcleaner = TextCleaner()
device = 'cuda' if torch.cuda.is_available() else 'cpu'

# AIHUB_ML 체크포인트가 학습된 다국어 토큰 vocabulary.
# 반드시 저장소의 symbols.py를 써야 함. text_utils.py의 영어 전용 집합이 아님.
from symbols import symbols
word_index_dictionary = {s: i for i, s in enumerate(symbols)}

# 한국어 G2P(grapheme-to-phoneme).
# 학습 데이터 filelist가 g2pk로 만들어졌으므로, 추론에서도 자모 분해 전에 동일한
# 발음 규칙을 거쳐야 함(예: 같이 -> 가치). 안 그러면 발음이 어긋남.
# g2pk나 그 mecab+java 백엔드를 못 쓰면 raw 자모 분해로 자동 강등.
try:
    from g2pk import G2p
    _g2p = G2p()
    print("g2pk loaded for Korean grapheme-to-phoneme.")
except Exception as e:
    _g2p = None
    print(f"WARNING: g2pk unavailable ({e}); falling back to raw jamo decomposition.")

# Torchaudio 로드
import torchaudio
to_mel = torchaudio.transforms.MelSpectrogram(n_mels=80, n_fft=2048, win_length=1200, hop_length=300)
mean, std = -4, 4

def preprocess(wave):
    wave_tensor = torch.from_numpy(wave).float()
    mel_tensor = to_mel(wave_tensor)
    mel_tensor = (torch.log(1e-5 + mel_tensor.unsqueeze(0)) - mean) / std
    return mel_tensor

import hashlib
_style_cache = {}  # 캐시 키 -> 참조 스타일 텐서. 같은 요청 반복 시 재인코딩 생략

SR = 24000
MIN_REF_SEC = 3.0    # 이보다 짧으면 style encoder가 화자 특성을 못 잡아냄
MAX_REF_SEC = 15.0   # 이보다 길면 잘라냄. OOM 방지 + 스타일 과평균화 방지

def _style_from_wave(wave):
    audio, _ = librosa.effects.trim(wave, top_db=30)
    mel_tensor = preprocess(audio).to(device)
    with torch.no_grad():
        ref_s = model.style_encoder(mel_tensor.unsqueeze(1))
        ref_p = model.predictor_encoder(mel_tensor.unsqueeze(1))
    return torch.cat([ref_s, ref_p], dim=1)

def compute_style(path):
    wave, _ = librosa.load(path, sr=SR)
    return _style_from_wave(wave)

def compute_style_from_bytes(raw, ref_start=None, ref_end=None):
    key = f"{hashlib.sha1(raw).hexdigest()}:{ref_start}:{ref_end}"
    if key in _style_cache:
        return _style_cache[key]

    # librosa는 실제 파일 "경로"를 받았을 때만 ffmpeg/audioread 디코더로 폴백함.
    # BytesIO를 넘기면 soundfile 에러를 그대로 다시 던져서 m4a/mp3가 실패함.
    # 그래서 임시 파일로 쓴 뒤 경로로 로드해야 ffmpeg가 해당 포맷을 처리할 수 있음.
    import tempfile
    tmp_path = None
    try:
        with tempfile.NamedTemporaryFile(suffix=".audio", delete=False) as tf:
            tf.write(raw)
            tmp_path = tf.name
        wave, _ = librosa.load(tmp_path, sr=SR)
    except Exception:
        raise HTTPException(
            status_code=400,
            detail="오디오 포맷을 인식할 수 없습니다. WAV/FLAC/OGG/Opus/MP3/M4A 파일을 사용하세요 "
                   "(손상되었거나 지원하지 않는 형식일 수 있습니다)."
        )
    finally:
        if tmp_path:
            try:
                os.remove(tmp_path)
            except OSError:
                pass
    dur = len(wave) / SR

    if dur < MIN_REF_SEC:
        raise HTTPException(
            status_code=400,
            detail=f"참조 오디오가 너무 짧습니다. 최소 {MIN_REF_SEC:.0f}초 필요 (현재 {dur:.1f}초)."
        )

    if ref_start is not None or ref_end is not None:
        # 호출측이 구간을 명시한 경우
        start = ref_start if ref_start is not None else 0.0
        end = ref_end if ref_end is not None else dur
        if start < 0 or end > dur + 0.05 or start >= end:
            raise HTTPException(
                status_code=400,
                detail=f"참조 구간이 올바르지 않습니다. 0 ~ {dur:.1f}초 범위에서 시작<끝 이어야 합니다 (요청: {start:.1f}~{end:.1f})."
            )
        if (end - start) < MIN_REF_SEC:
            raise HTTPException(
                status_code=400,
                detail=f"선택 구간이 너무 짧습니다. 최소 {MIN_REF_SEC:.0f}초 (현재 {end - start:.1f}초)."
            )
        end = min(end, start + MAX_REF_SEC)  # 구간 길이 상한 적용
        wave = wave[int(start * SR):int(end * SR)]
    elif dur > MAX_REF_SEC:
        # 긴 파일인데 구간 지정이 없으면 안전 기본값으로 앞 MAX_REF_SEC초만 사용
        wave = wave[:int(MAX_REF_SEC * SR)]

    style = _style_from_wave(wave)
    _style_cache[key] = style
    return style

# 정상적으로 샘플링된 스타일은 참조 스타일에서 크게 벗어나지 않음
# (실측: |s_pred - ref_s| 가 ref_s 노름의 0.6~1.0배). 이 배수를 넘으면 발산한 것으로 봄.
STYLE_DIVERGENCE_LIMIT = 2.0
STYLE_SAMPLE_RETRIES = 3

def _style_is_sane(s_pred, ref_s):
    if not torch.isfinite(s_pred).all():
        return False
    return float((s_pred - ref_s).norm()) <= STYLE_DIVERGENCE_LIMIT * float(ref_s.norm())

def sample_style(embedding, ref_s, embedding_scale, num_steps):
    # embedding_scale이 1.0이 아니면 sampler가 classifier-free guidance 경로를 선택하는데,
    # 이 체크포인트에서는 그 경로가 무작위로 발산함(1.1에서도 약 3번에 1번).
    # 발산한 스타일이 그대로 쓰이면 길이 예측이 폭주해서 10~30초짜리 잡음이 나옴.
    # 그래서 결과를 검사해 발산했으면 다시 만들고, 계속 실패하면 안정적인 1.0으로 만듦.
    def draw(scale):
        return sampler(noise=torch.randn((1, 256)).unsqueeze(1).to(device),
                       embedding=embedding, embedding_scale=scale,
                       features=ref_s, num_steps=num_steps).squeeze(1)

    for _ in range(STYLE_SAMPLE_RETRIES):
        s_pred = draw(embedding_scale)
        if _style_is_sane(s_pred, ref_s):
            return s_pred

    if embedding_scale != 1.0:
        print(f"Style sampling diverged at embedding_scale={embedding_scale}; "
              f"falling back to 1.0.", flush=True)
        s_pred = draw(1.0)
        if _style_is_sane(s_pred, ref_s):
            return s_pred

    # 그래도 안 되면 diffusion 결과를 버리고 참조 스타일만 사용
    print("Style sampling diverged; using the reference style only.", flush=True)
    return ref_s.clone()

def length_to_mask(lengths):
    mask = torch.arange(lengths.max()).unsqueeze(0).expand(lengths.shape[0], -1).type_as(lengths)
    mask = torch.gt(mask+1, lengths.unsqueeze(1))
    return mask

@app.on_event("startup")
def load_model():
    global model, config, sampler
    model_dir = "./models/Korean/Vocos/AIHUB_ML"
    print(f"Starting TTS Server on {device}...")

    config_path = os.path.join(model_dir, "config_aihub_multi_lingual_en_jp_ko_zh_vocos.yml")
    ckpt_path = os.path.join(model_dir, "epoch_2nd_00006.pth")

    if not os.path.exists(config_path) or not os.path.exists(ckpt_path):
        print(f"Warning: Model files not found in {model_dir}.")
        return

    try:
        config = yaml.safe_load(open(config_path))
        model_params = recursive_munch(config['model_params'])

        # 모델 빌드.
        # Vocos 파인튜닝에서 aligner가 없는 경우 build_model이 None을 받아도 되는 것으로 간주.
        model = build_model_no_bert(model_params, None, None)

        params_whole = torch.load(ckpt_path, map_location='cpu')
        params = params_whole['net'] if 'net' in params_whole else params_whole

        # 중요: strict=True로 로드하고, 체크포인트의 키 이름이 현재 모듈과 다르면
        # 순서대로 재매핑함. 레퍼런스 Demo/Inference_stage2.ipynb와 동일한 방식.
        # 여기서 strict=False는 조용한 함정임. 키 이름이 어긋나면 가중치를 0개 로드하고도
        # 성공한 척 넘어가고, 모듈은 랜덤 초기화 상태로 남음. 그 결과 길이, 에너지는
        # 멀쩡한데 내용은 순수 잡음인 출력이 나옴.
        from collections import OrderedDict
        loaded, remapped, missing = [], [], []
        for key in model:
            if model[key] is None:
                continue
            if key not in params:
                missing.append(key)
                continue
            try:
                model[key].load_state_dict(params[key], strict=True)
                loaded.append(key)
            except Exception:
                state_dict = params[key]
                new_state_dict = OrderedDict()
                for (k_m, _), (k_c, v_c) in zip(model[key].state_dict().items(), state_dict.items()):
                    new_state_dict[k_m] = v_c
                # strict=True로 둬야 진짜 불일치일 때 조용히 잡음을 로드하지 않고 즉시 에러가 남
                model[key].load_state_dict(new_state_dict, strict=True)
                remapped.append(key)
        load_report["loaded"] = loaded
        load_report["remapped"] = remapped
        load_report["missing"] = missing
        print(f"Weights loaded (strict): {loaded}", flush=True)
        print(f"Weights loaded (positional remap): {remapped}", flush=True)
        if missing:
            print(f"WARNING: modules not found in checkpoint (left untrained): {missing}", flush=True)

        _ = [model[key].eval() for key in model if model[key] is not None]
        _ = [model[key].to(device) for key in model if model[key] is not None]

        sampler = DiffusionSampler(
            model.diffusion.diffusion,
            sampler=ADPM2Sampler(),
            sigma_schedule=KarrasSchedule(sigma_min=0.0001, sigma_max=3.0, rho=9.0),
            clamp=False
        )
        print("Model loaded successfully.", flush=True)
    except Exception as e:
        import traceback
        load_report["error"] = str(e)
        print(f"Error loading model: {e}", flush=True)
        traceback.print_exc()

@app.get("/health")
def health():
    # stdout 버퍼링과 무관하게 로딩 시점에 무슨 일이 있었는지 확인하는 용도
    expected = ["decoder", "predictor", "text_encoder", "prosodic_text_encoder",
                "style_encoder", "predictor_encoder", "diffusion"]
    ok_modules = set(load_report["loaded"]) | set(load_report["remapped"])
    return {
        "model_loaded": model is not None,
        "device": device,
        "g2p": _g2p is not None,
        "all_core_modules_loaded": all(m in ok_modules for m in expected),
        "missing_core_modules": [m for m in expected if m not in ok_modules],
        **load_report,
    }

@app.post("/tts")
async def generate_tts(request: TTSRequest):
    text = request.text
    speaker_id = request.speaker_id

    if not text:
        raise HTTPException(status_code=400, detail="Text is empty")
    if model is None:
        raise HTTPException(status_code=500, detail="Model is not loaded")

    try:
        # 참조 스타일 결정.
        # 업로드된 오디오를 우선 사용(클라이언트 파일이 어디 있든 동작함).
        # 서버측 경로는 컨테이너 안에 실제로 존재할 때만 폴백으로 사용.
        if request.speaker_wav_b64:
            import base64
            try:
                raw = base64.b64decode(request.speaker_wav_b64)
            except Exception:
                raise HTTPException(status_code=400, detail="speaker_wav_b64 is not valid base64")
            ref_s = compute_style_from_bytes(raw, request.ref_start, request.ref_end)
        elif speaker_id and speaker_id != "0" and os.path.exists(speaker_id):
            ref_s = compute_style(speaker_id)
        else:
            raise HTTPException(
                status_code=400,
                detail="No reference voice provided. Upload a wav via speaker_wav_b64 "
                       "(client should read the local file and send its bytes); a local "
                       "PC path in speaker_id cannot be seen from inside the container."
            )

        from jamo import h2j

        # 먼저 한국어 발음 규칙 적용(학습 전처리와 동일하게 맞춤).
        # 그 다음 조합용 자모(U+1100 블록)로 분해 - 다국어 모델이 학습된 심볼이 이것임.
        cleaned = _g2p(text) if _g2p is not None else text
        jamo_text = h2j(cleaned)
        tokens = []
        for char in jamo_text:
            if char in word_index_dictionary:
                tokens.append(word_index_dictionary[char])

        tokens.insert(0, 0)
        tokens.append(0)
        tokens = torch.LongTensor(tokens).to(device).unsqueeze(0)

        with torch.no_grad():
            input_lengths = torch.LongTensor([tokens.shape[-1]]).to(device)
            text_mask = length_to_mask(input_lengths).to(device)

            t_en = model.text_encoder(tokens, input_lengths, text_mask)
            d_en = model.prosodic_text_encoder(tokens, input_lengths, text_mask)
            d_en_dur = d_en.transpose(-1, -2)

            alpha = request.alpha
            beta = request.beta

            s_pred = sample_style(d_en_dur, ref_s, request.embedding_scale, request.diffusion_steps)

            s = s_pred[:, 128:]
            ref = s_pred[:, :128]

            # diffusion이 예측한 스타일과 참조 화자의 스타일을 블렌딩.
            # alpha/beta가 낮을수록 업로드된 목소리에 가까워짐.
            ref = alpha * ref + (1 - alpha) * ref_s[:, :128]
            s = beta * s + (1 - beta) * ref_s[:, 128:]

            d = model.predictor.text_encoder(d_en, s, input_lengths, text_mask)

            x, _ = model.predictor.lstm(d)
            duration = model.predictor.duration_proj(x)
            duration = torch.sigmoid(duration).sum(axis=-1)
            # speed로 나누므로 >1이면 길이가 짧아져 말이 빨라짐
            speed = max(request.speed, 0.1)
            pred_dur = torch.round(duration.squeeze() / speed).clamp(min=1)

            pred_aln_trg = torch.zeros(input_lengths, int(pred_dur.sum().data))
            c_frame = 0
            for i in range(pred_aln_trg.size(0)):
                pred_aln_trg[i, c_frame:c_frame + int(pred_dur[i].data)] = 1
                c_frame += int(pred_dur[i].data)

            en = (d.transpose(-1, -2) @ pred_aln_trg.unsqueeze(0).to(device))
            # 필수 프레임 시프트.
            # 레퍼런스 no-BERT 추론은 이 처리를 조건 없이 무조건 적용함(hifigan 전용이 아님).
            # Vocos 디코더는 이 시프트된 정렬을 전제로 학습됐기 때문에, 생략하면 운율과
            # 음향 프레임이 어긋나 음성이 잡음으로 변함.
            en_shift = torch.zeros_like(en)
            en_shift[:, :, 0] = en[:, :, 0]
            en_shift[:, :, 1:] = en[:, :, 0:-1]
            en = en_shift

            F0_pred, N_pred = model.predictor.F0Ntrain(en, s)

            asr = (t_en @ pred_aln_trg.unsqueeze(0).to(device))
            asr_shift = torch.zeros_like(asr)
            asr_shift[:, :, 0] = asr[:, :, 0]
            asr_shift[:, :, 1:] = asr[:, :, 0:-1]
            asr = asr_shift

            out = model.decoder(asr, F0_pred, N_pred, ref.squeeze().unsqueeze(0))
            audio_data = out.squeeze().cpu().numpy()[..., :-50]

        # 클리핑과 wrap-around 잡음(static)을 막기 위해 정규화
        max_val = np.max(np.abs(audio_data))
        if max_val > 0:
            audio_data = audio_data / max_val

        # 메모리 상에서 WAV로 변환
        audio_data = np.int16(audio_data * 32767)
        buffer = io.BytesIO()
        wavf.write(buffer, 24000, audio_data)
        buffer.seek(0)

        return Response(content=buffer.read(), media_type="audio/wav")

    except HTTPException:
        raise
    except Exception as e:
        print(f"Inference error: {e}")
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=10000)
