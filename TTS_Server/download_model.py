import os
from huggingface_hub import snapshot_download

MODEL_ID = "5Hyeons/StyleTTS2"
LOCAL_DIR = "./models/Korean"
TARGET_DIR = os.path.join(LOCAL_DIR, "Vocos", "AIHUB_ML")

CKPT = os.path.join(TARGET_DIR, "epoch_2nd_00006.pth")
CONFIG = os.path.join(TARGET_DIR, "config_aihub_multi_lingual_en_jp_ko_zh_vocos.yml")

# 정상 체크포인트는 약 2.48GB. 이보다 한참 작으면 다운로드가 잘린 파일임.
MIN_CKPT_BYTES = 1_000_000_000


def already_downloaded():
    if not (os.path.exists(CKPT) and os.path.exists(CONFIG)):
        return False
    size = os.path.getsize(CKPT)
    if size < MIN_CKPT_BYTES:
        print(f"Checkpoint looks truncated ({size / 1e9:.2f} GB); re-downloading.", flush=True)
        return False
    return True


def download():
    # ./models는 바인드 마운트라 컨테이너를 재생성해도 파일이 남음.
    # 하지만 Hugging Face 캐시는 컨테이너 "내부"의 ~/.cache/huggingface에 있어서 컨테이너를 다시 만들면 함께 지워짐.
    # 그 캐시가 없으면 snapshot_download는 파일이 이미 있는지 알 수 없어서
    # `docker compose up` 할 때마다 2.48GB를 통째로 다시 받음.
    # 파일을 직접 확인해야 스킵이 확실하게 동작함.
    if already_downloaded():
        print(f"Model already present in {TARGET_DIR} — skipping download.", flush=True)
        return

    print(f"Downloading model {MODEL_ID} from Hugging Face...", flush=True)
    os.makedirs(LOCAL_DIR, exist_ok=True)

    snapshot_download(
        repo_id=MODEL_ID,
        local_dir=LOCAL_DIR,
        local_dir_use_symlinks=False,
        allow_patterns=["Vocos/AIHUB_ML/*.yml", "Vocos/AIHUB_ML/epoch_2nd_*.pth"],
    )
    print("Model downloaded successfully!", flush=True)


if __name__ == "__main__":
    download()
