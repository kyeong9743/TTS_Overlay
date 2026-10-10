using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TtsOverlay.Models;
using TtsOverlay.Services;

namespace TtsOverlay
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;
        private AudioPlaybackService _audioService;
        private TtsClientService _ttsClient;
        private bool _isPreviewing = false;

        // 캡처 칸에 현재 표시 중인 단축키 조합. 저장 버튼을 눌러야 실제로 적용됨.
        private uint _hotkeyModifiers;
        private uint _hotkeyKey;
        private uint _stopHotkeyModifiers;
        private uint _stopHotkeyKey;

        public SettingsWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            _audioService = new AudioPlaybackService();
            _ttsClient = new TtsClientService();

            LoadSettings();

            // 단축키 칸에 포커스를 둔 채 창을 닫아도 전역 단축키가 다시 살아나도록 함
            Closed += (s, e) => (Owner as MainWindow)?.ResumeHotkey();
        }

        private void LoadSettings()
        {
            ApiUrlTextBox.Text = _settings.TtsApiUrl;
            // 화면에는 전체 경로로 보여 주고, 저장할 때 다시 상대/전체 경로로 정리함
            SpeakerIdTextBox.Text = _settings.SpeakerPath;
            RefStartTextBox.Text = _settings.RefEndSec > 0 ? _settings.RefStartSec.ToString("0.#") : "";
            RefEndTextBox.Text = _settings.RefEndSec > 0 ? _settings.RefEndSec.ToString("0.#") : "";
            SpotlightModeCheckBox.IsChecked = _settings.UseSpotlightMode;
            MonitorOnSendCheckBox.IsChecked = _settings.MonitorOnSend;

            _hotkeyModifiers = _settings.HotkeyModifiers;
            _hotkeyKey = _settings.HotkeyKey;
            HotkeyTextBox.Text = HotkeyManager.Format(_hotkeyModifiers, _hotkeyKey);
            _stopHotkeyModifiers = _settings.StopHotkeyModifiers;
            _stopHotkeyKey = _settings.StopHotkeyKey;
            StopHotkeyTextBox.Text = HotkeyManager.Format(_stopHotkeyModifiers, _stopHotkeyKey);
            VolumeSlider.Value = _settings.Volume;

            var devices = _audioService.GetOutputDevices();
            DeviceComboBox.ItemsSource = devices;

            if (devices.Contains(_settings.OutputDeviceName))
            {
                DeviceComboBox.SelectedItem = _settings.OutputDeviceName;
            }
            else if (devices.Count > 0)
            {
                DeviceComboBox.SelectedIndex = 0;
            }

            // 듣기 장치. 목록의 첫 항목(사운드 매퍼)이 곧 시스템 기본 장치
            MonitorDeviceComboBox.ItemsSource = devices;
            if (devices.Contains(_settings.MonitorDeviceName))
            {
                MonitorDeviceComboBox.SelectedItem = _settings.MonitorDeviceName;
            }
            else if (devices.Count > 0)
            {
                MonitorDeviceComboBox.SelectedIndex = 0;
            }

            // 음성 복제 튜닝 슬라이더
            AlphaSlider.Value = _settings.Alpha;
            BetaSlider.Value = _settings.Beta;
            StepsSlider.Value = _settings.DiffusionSteps;
            SpeedSlider.Value = _settings.Speed;

            AlphaSlider.ValueChanged += (s, e) => AlphaLabel.Text = $"음색 유사도 (alpha): {AlphaSlider.Value:0.00}";
            BetaSlider.ValueChanged += (s, e) => BetaLabel.Text = $"억양 유사도 (beta): {BetaSlider.Value:0.00}";
            StepsSlider.ValueChanged += (s, e) => StepsLabel.Text = $"품질/노이즈 제거 (diffusion steps): {(int)StepsSlider.Value}";
            SpeedSlider.ValueChanged += (s, e) => SpeedLabel.Text = $"말하는 속도 (speed): {SpeedSlider.Value:0.0}배";
            VolumeSlider.ValueChanged += (s, e) => VolumeLabel.Text = $"음성 출력 볼륨 조절: {VolumeSlider.Value * 100:0}%";
            VolumeLabel.Text = $"음성 출력 볼륨 조절: {VolumeSlider.Value * 100:0}%";

            // 라벨을 현재 값으로 초기화
            AlphaLabel.Text = $"음색 유사도 (alpha): {AlphaSlider.Value:0.00}";
            BetaLabel.Text = $"억양 유사도 (beta): {BetaSlider.Value:0.00}";
            StepsLabel.Text = $"품질/노이즈 제거 (diffusion steps): {(int)StepsSlider.Value}";
            SpeedLabel.Text = $"말하는 속도 (speed): {SpeedSlider.Value:0.0}배";
        }

        private async void PreviewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isPreviewing) return;
            
            _isPreviewing = true;
            PreviewButton.Content = "재생 중...";
            PreviewButton.IsEnabled = false;

            try
            {
                // 미리듣기용 테스트 문장
                string testText = "안녕하세요, 목소리 테스트입니다.";
                string apiUrl = ApiUrlTextBox.Text;
                string speakerId = AppSettings.ResolvePath(SpeakerIdTextBox.Text);
                if (!CheckSpeakerFile(speakerId)) return;

                var (refStart, refEnd) = GetRefWindow();

                var audioStream = await _ttsClient.GenerateAudioAsync(
                    apiUrl, testText, speakerId,
                    AlphaSlider.Value, BetaSlider.Value, (int)StepsSlider.Value,
                    SpeedSlider.Value, refStart, refEnd);

                if (audioStream != null)
                {
                    // 송출 장치가 아닌 듣기 장치로만 재생.
                    // 미리듣기는 방송에 나가면 안 되므로 내 스피커로만 들려줌.
                    float volume = (float)VolumeSlider.Value;
                    await _audioService.PlayAudioAsync(audioStream, SelectedMonitorDevice(), volume);
                }
                else
                {
                    MessageBox.Show("API URL 또는 서버를 확인하세요.", "미리듣기 실패");
                }
            }
            catch (System.Exception ex)
            {
                // 서버가 보낸 경고(예: "참조 오디오가 너무 짧습니다...")를 사용자에게 표시
                MessageBox.Show(ex.Message, "미리듣기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _isPreviewing = false;
                PreviewButton.Content = "▶ 미리듣기";
                PreviewButton.IsEnabled = true;
            }
        }

        // 선택된 듣기 장치 이름. 첫 항목(사운드 매퍼 = 시스템 기본 장치)이면 빈 문자열.
        private string SelectedMonitorDevice()
        {
            if (MonitorDeviceComboBox.SelectedIndex <= 0) return "";
            return MonitorDeviceComboBox.SelectedItem as string ?? "";
        }

        private void HotkeyTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            // 입력받는 동안에는 현재 전역 단축키를 꺼 둠. 켜져 있으면 그 조합이 이 칸에 전달되지 않음.
            (Owner as MainWindow)?.SuspendHotkey();
            ((TextBox)sender).Text = "조합을 누르세요...";
        }

        private void HotkeyTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 조합을 누르지 않고 다른 곳을 클릭했다면 표시를 원래대로 복구
            HotkeyTextBox.Text = HotkeyManager.Format(_hotkeyModifiers, _hotkeyKey);
            StopHotkeyTextBox.Text = HotkeyManager.Format(_stopHotkeyModifiers, _stopHotkeyKey);
            (Owner as MainWindow)?.ResumeHotkey();
        }

        private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;   // 키 입력이 텍스트박스나 다이얼로그로 전달되지 않게 막음

            // Alt 조합은 Key.System으로 들어오고, 실제 키는 SystemKey에 담김
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            // 수정자만 눌린 상태는 무시. 실제 키가 들어올 때까지 대기.
            if (HotkeyManager.IsModifierKey(key)) return;

            uint mods = HotkeyManager.CurrentModifiers();
            if (mods == 0)
            {
                MessageBox.Show(
                    "수정자 키(Ctrl / Alt / Shift / Win)를 최소 1개 포함해야 합니다.\n예: Ctrl+T, Ctrl+Shift+T",
                    "단축키 설정", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

            // 실제로 등록 가능한 조합인지 바로 시험. 안 되면 저장 전에 알려줌.
            if (!HotkeyManager.IsAvailable(new WindowInteropHelper(this).Handle, mods, vk))
            {
                MessageBox.Show(
                    $"{HotkeyManager.Format(mods, vk)} 은(는) Windows나 다른 프로그램이 이미 사용 중이라 등록할 수 없습니다.\n다른 조합을 눌러 주세요.",
                    "단축키 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 두 단축키(열기/닫기, 재생 중단)가 같은 조합이면 둘 중 하나만 등록되므로 막음
            bool isStopBox = ReferenceEquals(sender, StopHotkeyTextBox);
            bool sameAsOther = isStopBox
                ? (mods == _hotkeyModifiers && vk == _hotkeyKey)
                : (mods == _stopHotkeyModifiers && vk == _stopHotkeyKey);
            if (sameAsOther)
            {
                MessageBox.Show(
                    $"{HotkeyManager.Format(mods, vk)} 은(는) 다른 단축키에 이미 지정되어 있습니다.\n서로 다른 조합을 사용해 주세요.",
                    "단축키 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (isStopBox)
            {
                _stopHotkeyModifiers = mods;
                _stopHotkeyKey = vk;
                StopHotkeyTextBox.Text = HotkeyManager.Format(mods, vk);
            }
            else
            {
                _hotkeyModifiers = mods;
                _hotkeyKey = vk;
                HotkeyTextBox.Text = HotkeyManager.Format(mods, vk);
            }
        }

        private void ResetStopHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            _stopHotkeyModifiers = HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT;
            _stopHotkeyKey = 0x58; // 'X'
            StopHotkeyTextBox.Text = HotkeyManager.Format(_stopHotkeyModifiers, _stopHotkeyKey);
        }

        private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            _hotkeyModifiers = HotkeyManager.DEFAULT_MODIFIERS;
            _hotkeyKey = HotkeyManager.DEFAULT_KEY;
            HotkeyTextBox.Text = HotkeyManager.Format(_hotkeyModifiers, _hotkeyKey);
        }

        // 참조 오디오 사용 구간(초)을 반환. 미지정이면 (null, null).
        // 끝 값이 0보다 클 때만 구간이 실제로 적용됨.
        private (double? start, double? end) GetRefWindow()
        {
            bool hasEnd = double.TryParse(RefEndTextBox.Text, out double end);
            if (!hasEnd || end <= 0) return (null, null);
            double start = double.TryParse(RefStartTextBox.Text, out double s) ? s : 0.0;
            return (start, end);
        }

        private void BrowseSpeakerButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "목소리 복제용 오디오 파일 선택",
                // 지원 확장자만 보이게 함("모든 파일" 항목 없음)
                Filter = "오디오 파일 (*.wav;*.flac;*.ogg;*.opus;*.mp3;*.m4a)|*.wav;*.flac;*.ogg;*.opus;*.mp3;*.m4a|"
                       + "WAV (*.wav)|*.wav|FLAC (*.flac)|*.flac|Opus/OGG (*.opus;*.ogg)|*.opus;*.ogg|"
                       + "MP3 (*.mp3)|*.mp3|M4A/AAC (*.m4a)|*.m4a"
            };

            // 현재 지정된 파일의 폴더에서 시작하고, 없으면 기본 voice 폴더에서 시작
            string currentDir = "";
            try { currentDir = System.IO.Path.GetDirectoryName(AppSettings.ResolvePath(SpeakerIdTextBox.Text)) ?? ""; } catch { }
            if (System.IO.Directory.Exists(currentDir))
                dialog.InitialDirectory = currentDir;
            else if (System.IO.Directory.Exists(AppSettings.VoiceFolder))
                dialog.InitialDirectory = AppSettings.VoiceFolder;

            if (dialog.ShowDialog() == true && CheckSpeakerFile(dialog.FileName))
            {
                SpeakerIdTextBox.Text = dialog.FileName;
            }
        }

        // 지원하는 오디오 확장자인지 확인. 아니면 안내를 띄우고 false.
        // 찾아보기 창에 파일 이름을 직접 입력하거나 경로 칸에 직접 타이핑한 경우까지 막기 위한 것.
        private bool CheckSpeakerFile(string path)
        {
            if (AppSettings.IsSupportedAudioFile(path)) return true;

            MessageBox.Show(
                "지원하지 않는 파일 형식입니다.\n사용할 수 있는 확장자: "
                + string.Join(", ", AppSettings.SupportedAudioExtensions),
                "목소리 오디오 파일", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string speakerPath = SpeakerIdTextBox.Text.Trim();
            if (!CheckSpeakerFile(speakerPath)) return;   // 저장하지 않고 설정 창에 머무름

            _settings.TtsApiUrl = ApiUrlTextBox.Text;
            _settings.SpeakerId = AppSettings.ToStoredPath(speakerPath);
            var (refStart, refEnd) = GetRefWindow();
            _settings.RefStartSec = refStart ?? 0.0;
            _settings.RefEndSec = refEnd ?? 0.0;
            _settings.OutputDeviceName = DeviceComboBox.SelectedItem as string ?? "";
            _settings.UseSpotlightMode = SpotlightModeCheckBox.IsChecked ?? false;
            _settings.MonitorOnSend = MonitorOnSendCheckBox.IsChecked ?? false;
            _settings.MonitorDeviceName = SelectedMonitorDevice();
            _settings.Volume = (float)VolumeSlider.Value;

            _settings.HotkeyModifiers = _hotkeyModifiers;
            _settings.HotkeyKey = _hotkeyKey;
            _settings.StopHotkeyModifiers = _stopHotkeyModifiers;
            _settings.StopHotkeyKey = _stopHotkeyKey;

            _settings.Alpha = AlphaSlider.Value;
            _settings.Beta = BetaSlider.Value;
            _settings.DiffusionSteps = (int)StepsSlider.Value;
            _settings.Speed = SpeedSlider.Value;

            _settings.Save();
            
            // MainWindow가 열려 있으면 설정을 다시 적용
            if (Application.Current.MainWindow is MainWindow main)
            {
                main.ApplySettings(_settings);
            }

            this.Close();
        }
    }
}
