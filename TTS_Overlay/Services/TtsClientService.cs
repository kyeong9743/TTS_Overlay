using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TtsOverlay.Services
{
    public class TtsClientService
    {
        private readonly HttpClient _httpClient;

        public TtsClientService()
        {
            _httpClient = new HttpClient();
        }

        public async Task<Stream> GenerateAudioAsync(string apiUrl, string text, string speakerId = "0",
            double alpha = 0.1, double beta = 0.5, int diffusionSteps = 10,
            double speed = 1.0, double? refStart = null, double? refEnd = null)
        {
            if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(text))
                return null;

            // 서버는 Docker 컨테이너 안에서 돌기 때문에 이 PC의 파일시스템을 볼 수 없음.
            // 그래서 경로가 아니라 파일 내용을 읽어서 업로드함.
            // 파일이 없으면 서버까지 가지 않고 여기서 바로 알려줌. 그대로 보내면 서버가
            // "No reference voice provided" 라는 영어 오류만 돌려줘서 원인을 알기 어려움.
            if (string.IsNullOrWhiteSpace(speakerId) || !File.Exists(speakerId))
            {
                string shown = string.IsNullOrWhiteSpace(speakerId) ? "(지정되지 않음)" : speakerId;
                throw new Exception(
                    "목소리 오디오 파일을 찾을 수 없습니다.\n\n" + shown + "\n\n"
                    + "설정(⚙)의 '목소리 오디오 파일'에서 찾아보기로 파일을 지정하거나,\n"
                    + "프로그램 폴더의 voice 폴더에 voice.opus 파일을 넣어 주세요.");
            }

            var wavBytes = File.ReadAllBytes(speakerId);
            string speakerWavB64 = Convert.ToBase64String(wavBytes);

            var payload = new
            {
                text = text,
                speaker_id = speakerId,
                speaker_wav_b64 = speakerWavB64,
                alpha = alpha,
                beta = beta,
                diffusion_steps = diffusionSteps,
                // 1.0 고정. 이 체크포인트는 1.0이 아니면 스타일 샘플링이 발산해 잡음이 나옴.
                embedding_scale = 1.0,
                speed = speed,
                ref_start = refStart,
                ref_end = refEnd
            };
            var jsonPayload = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(apiUrl, content);
            if (!response.IsSuccessStatusCode)
            {
                // 서버가 보낸 메시지(예: "참조 오디오가 너무 짧습니다...")를 그대로 올려보냄.
                // 호출측이 이를 사용자에게 표시할 수 있게 하려는 것. 조용히 실패하지 않도록.
                var body = await response.Content.ReadAsStringAsync();
                string detail = body;
                try
                {
                    var token = JObject.Parse(body)["detail"];
                    // 입력값 검증 오류(422)는 detail이 배열로 옴. 사람이 읽을 수 있게 메시지만 추림.
                    if (token is JArray items)
                        detail = string.Join("\n", items.Select(i => $"{i["loc"]?.Last}: {i["msg"]}"));
                    else
                        detail = token?.ToString() ?? body;
                }
                catch { }
                throw new Exception(detail);
            }

            var audioBytes = await response.Content.ReadAsByteArrayAsync();
            return new MemoryStream(audioBytes);
        }
    }
}
