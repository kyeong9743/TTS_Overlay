using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TtsOverlay.Models
{
    public class AppSettings
    {
        public string TtsApiUrl { get; set; } = "http://127.0.0.1:10000/tts";
        public string OutputDeviceName { get; set; } = "";
        public bool UseSpotlightMode { get; set; } = false;
        // 목소리(참조) 오디오 파일 경로. 기본값은 실행 폴더의 voice\voice.opus.
        // 실행 폴더 안의 파일은 상대 경로("voice\\voice.opus")로 저장해서, 앱 폴더를 통째로 옮기거나
        // 다른 PC에 복사해도 그대로 찾을 수 있게 함. 실행 폴더 밖의 파일은 전체 경로로 저장.
        // 실제 파일을 열 때는 SpeakerPath(전체 경로)를 쓸 것.
        public string SpeakerId { get; set; } = ToStoredPath(DefaultSpeakerPath);

        /// <summary>SpeakerId를 전체 경로로 풀어낸 값.</summary>
        [JsonIgnore]
        public string SpeakerPath => ResolvePath(SpeakerId);

        // 서버가 디코딩할 수 있는 참조 오디오 확장자. 이 외의 파일은 설정에 등록할 수 없음.
        public static readonly string[] SupportedAudioExtensions =
            { ".wav", ".flac", ".ogg", ".opus", ".mp3", ".m4a" };

        /// <summary>목소리 파일을 두는 기본 폴더(실행 폴더\voice).</summary>
        public static string VoiceFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voice");

        /// <summary>기본 목소리 파일(실행 폴더\voice\voice.opus).</summary>
        public static string DefaultSpeakerPath => Path.Combine(VoiceFolder, "voice.opus");

        private static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>저장용 경로로 변환. 실행 폴더 안이면 상대 경로, 밖이면 전체 경로.</summary>
        public static string ToStoredPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            path = path.Trim();
            try
            {
                if (!Path.IsPathRooted(path)) return path;   // 이미 상대 경로

                string full = Path.GetFullPath(path);
                string baseDir = Path.GetFullPath(BaseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(baseDir.Length);
                return full;
            }
            catch (Exception)
            {
                return path;   // 경로로 해석할 수 없는 문자열은 그대로 둠
            }
        }

        /// <summary>저장된 경로를 전체 경로로 변환. 상대 경로는 실행 폴더 기준.</summary>
        public static string ResolvePath(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return "";
            stored = stored.Trim();
            try
            {
                return Path.IsPathRooted(stored) ? stored : Path.GetFullPath(Path.Combine(BaseDir, stored));
            }
            catch (Exception)
            {
                return stored;
            }
        }

        public static bool IsSupportedAudioFile(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            SupportedAudioExtensions.Contains(Path.GetExtension(path.Trim()).ToLowerInvariant());

        // 전역 단축키 (Win32 RegisterHotKey).
        // Modifiers는 MOD_CONTROL(2) | MOD_ALT(1) | MOD_SHIFT(4) | MOD_WIN(8) 비트마스크,
        // Key는 virtual-key 코드. 기본값은 Ctrl + T.
        public uint HotkeyModifiers { get; set; } = 2;      // MOD_CONTROL
        public uint HotkeyKey { get; set; } = 0x54;         // 'T'
        // 재생 중단 단축키. 기본값은 Ctrl + Alt + X.
        public uint StopHotkeyModifiers { get; set; } = 3;  // MOD_CONTROL | MOD_ALT
        public uint StopHotkeyKey { get; set; } = 0x58;     // 'X'
        public double WindowTop { get; set; } = double.NaN;
        public double WindowLeft { get; set; } = double.NaN;
        public float Volume { get; set; } = 1.0f;
        // 송출시 듣기: 출력 장치(가상 케이블 등)로 보내는 소리를 내 기본 스피커로도 동시에 재생
        public bool MonitorOnSend { get; set; } = false;
        // 듣기 장치 이름. 비어 있으면 시스템 기본 장치
        public string MonitorDeviceName { get; set; } = "";

        // 음성 복제 튜닝값 (서버 기본값과 동일하게 맞춤).
        // Alpha/Beta가 낮을수록 참조 목소리에 가까워지고,
        // DiffusionSteps가 높을수록 노이즈가 줄지만 느려짐.
        public double Alpha { get; set; } = 0.1;
        public double Beta { get; set; } = 0.5;
        public int DiffusionSteps { get; set; } = 10;
        public double Speed { get; set; } = 1.0;   // >1이면 말이 빨라짐
        // 참조 오디오에서 사용할 구간(초). wav가 15초를 넘을 때만 의미가 있음.
        // RefEndSec가 0 이하면 "미지정"이며, 이 경우 서버가 앞 15초를 자동으로 씀.
        public double RefStartSec { get; set; } = 0.0;
        public double RefEndSec { get; set; } = 0.0;

        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public static AppSettings Load()
        {
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (settings != null)
                    {
                        // 예전 기본값("0")이나 빈 값은 새 기본 경로로 바꿔 줌
                        if (string.IsNullOrWhiteSpace(settings.SpeakerId) || settings.SpeakerId == "0")
                            settings.SpeakerId = DefaultSpeakerPath;
                        // 예전 버전이 전체 경로로 저장해 둔 실행 폴더 안의 파일도 상대 경로로 정리
                        settings.SpeakerId = ToStoredPath(settings.SpeakerId);
                        return settings;
                    }
                }
                catch (Exception)
                {
                    // 오류는 무시하고 기본값 반환
                }
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception)
            {
                // 오류 무시
            }
        }
    }
}
