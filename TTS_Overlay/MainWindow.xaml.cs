using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TtsOverlay.Models;
using TtsOverlay.Services;

namespace TtsOverlay
{
    public partial class MainWindow : Window
    {
        private AppSettings _settings;
        private HotkeyManager _hotkeyManager;
        private HotkeyManager _stopHotkeyManager;
        private AudioPlaybackService _audioService;
        private TtsClientService _ttsClient;

        private bool _isProcessing = false;

        // RegisterHotKey가 실제로 수락한 마지막 조합. 잘못된 변경을 되돌릴 때 씀.
        private uint _activeModifiers;
        private uint _activeKey;
        private bool _hasActiveHotkey;

        public MainWindow()
        {
            InitializeComponent();
            
            _audioService = new AudioPlaybackService();
            _ttsClient = new TtsClientService();

            // 입력칸은 창 대부분을 차지하므로, 입력칸 위에서 우클릭해도 같은 메뉴가 뜨게 함
            // (기본 잘라내기/복사/붙여넣기 메뉴를 대체. Ctrl+C/V/X 단축키는 그대로 동작)
            InputTextBox.ContextMenu = OverlayMenu;
            
            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = AppSettings.Load();
            ApplySettings(_settings);
            
            if (!double.IsNaN(_settings.WindowTop) && !double.IsNaN(_settings.WindowLeft))
            {
                this.WindowStartupLocation = WindowStartupLocation.Manual;
                this.Top = _settings.WindowTop;
                this.Left = _settings.WindowLeft;
            }

            // 단축키 설정
            var helper = new WindowInteropHelper(this);
            _hotkeyManager = new HotkeyManager();
            _hotkeyManager.Setup(helper.Handle);
            _hotkeyManager.HotkeyPressed += HotkeyManager_HotkeyPressed;

            RegisterHotkeyFromSettings(showErrors: true);

            // 재생 중단 단축키. 같은 창에 걸지만 ID가 달라 서로 간섭하지 않음.
            _stopHotkeyManager = new HotkeyManager(HotkeyManager.STOP_HOTKEY_ID);
            _stopHotkeyManager.Setup(helper.Handle);
            _stopHotkeyManager.HotkeyPressed += (s, args) => AudioPlaybackService.StopAll();
            RegisterStopHotkey(showErrors: true);
        }

        /// <summary>설정값으로 재생 중단 단축키를 (재)등록.</summary>
        private void RegisterStopHotkey(bool showErrors)
        {
            if (_stopHotkeyManager == null) return;

            _stopHotkeyManager.Unregister();
            if (_stopHotkeyManager.Register(_settings.StopHotkeyModifiers, _settings.StopHotkeyKey)) return;

            if (showErrors)
            {
                string wanted = HotkeyManager.Format(_settings.StopHotkeyModifiers, _settings.StopHotkeyKey);
                MessageBox.Show(
                    $"재생 중단 단축키 {wanted} 등록에 실패했습니다.\n다른 프로그램이 이미 사용 중일 수 있습니다.\n설정에서 다른 조합을 지정하세요.",
                    "단축키 등록 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 설정값으로 전역 단축키를 (재)등록.
        /// 요청한 조합을 다른 앱이 이미 선점했다면, 마지막으로 성공했던 조합으로 되돌림.
        /// 사용자가 "단축키가 아예 없는 상태"에 빠지지 않게 하려는 것.
        /// </summary>
        public bool RegisterHotkeyFromSettings(bool showErrors = false)
        {
            if (_hotkeyManager == null) return false;

            _hotkeyManager.Unregister();

            if (_hotkeyManager.Register(_settings.HotkeyModifiers, _settings.HotkeyKey))
            {
                _activeModifiers = _settings.HotkeyModifiers;
                _activeKey = _settings.HotkeyKey;
                _hasActiveHotkey = true;
                return true;
            }

            // 등록 실패(조합 선점됨). 직전에 잘 되던 조합으로 복구.
            string wanted = HotkeyManager.Format(_settings.HotkeyModifiers, _settings.HotkeyKey);
            bool restored = _hasActiveHotkey && _hotkeyManager.Register(_activeModifiers, _activeKey);

            if (restored)
            {
                _settings.HotkeyModifiers = _activeModifiers;
                _settings.HotkeyKey = _activeKey;
                _settings.Save();
            }

            if (showErrors)
            {
                string tail = restored
                    ? $"\n이전 단축키({HotkeyManager.Format(_activeModifiers, _activeKey)})로 되돌렸습니다."
                    : "\n현재 등록된 단축키가 없습니다. 설정에서 다른 조합을 지정하세요.";

                MessageBox.Show(
                    $"단축키 {wanted} 등록에 실패했습니다.\n다른 프로그램이 이미 사용 중일 수 있습니다.{tail}",
                    "단축키 등록 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        /// <summary>
        /// 설정 창에서 새 단축키를 입력받는 동안 전역 단축키를 잠시 해제.
        /// 등록된 채로 두면 그 조합을 눌렀을 때 Windows가 키 입력을 가로채서
        /// 입력 칸에는 전달되지 않고 오버레이만 토글됨.
        /// </summary>
        public void SuspendHotkey()
        {
            _hotkeyManager?.Unregister();
            _stopHotkeyManager?.Unregister();
        }

        /// <summary>SuspendHotkey로 해제했던 단축키를 다시 등록.</summary>
        public void ResumeHotkey()
        {
            RegisterStopHotkey(showErrors: false);

            if (_hotkeyManager == null || !_hasActiveHotkey) return;
            _hotkeyManager.Unregister();
            _hotkeyManager.Register(_activeModifiers, _activeKey);
        }

        public void ApplySettings(AppSettings settings)
        {
            _settings = settings;

            // 단축키가 바뀌었을 수 있으므로 다시 바인딩.
            // manager가 아직 없으면(= MainWindow_Loaded의 최초 ApplySettings 호출 시점) 아무것도 안 함.
            RegisterHotkeyFromSettings(showErrors: true);
            RegisterStopHotkey(showErrors: true);

            ToggleModeMenuItem.Header = _settings.UseSpotlightMode ? "일반 창으로 전환" : "심플 창으로 전환";

            if (_settings.UseSpotlightMode)
            {
                this.Height = 80;
                this.Width = 600;
                HeaderGrid.Visibility = Visibility.Collapsed;
                SimpleSettingsButton.Visibility = Visibility.Visible;
                StatusText.Visibility = Visibility.Collapsed;
                MainBorder.CornerRadius = new CornerRadius(20);
                MainBorder.Padding = new Thickness(10);
                InputTextBox.FontSize = 24;
                InputTextBox.AcceptsReturn = false; // 심플 창은 한 줄만 입력됨
                InputTextBox.Margin = new Thickness(5);
            }
            else
            {
                this.Height = 150;
                this.Width = 600;
                HeaderGrid.Visibility = Visibility.Visible;
                SimpleSettingsButton.Visibility = Visibility.Collapsed;
                StatusText.Visibility = Visibility.Visible;
                MainBorder.CornerRadius = new CornerRadius(10);
                MainBorder.Padding = new Thickness(15);
                InputTextBox.FontSize = 18;
                InputTextBox.AcceptsReturn = true;
                InputTextBox.Margin = new Thickness(0);
            }
        }

        private void HotkeyManager_HotkeyPressed(object sender, EventArgs e)
        {
            if (this.IsVisible)
            {
                HideWindow();
            }
            else
            {
                ShowWindow();
            }
        }

        private void ShowWindow()
        {
            this.Show();
            this.Activate();
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        }

        private void HideWindow()
        {
            this.Hide();
            InputTextBox.Text = string.Empty;
        }

        private async void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                HideWindow();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && !_settings.UseSpotlightMode)
                {
                    // 일반 모드에서는 Shift+Enter로 줄바꿈 허용
                    return;
                }

                e.Handled = true;
                
                if (string.IsNullOrWhiteSpace(InputTextBox.Text)) return;
                
                if (_isProcessing) return;

                string textToSend = InputTextBox.Text;
                
                // 체감 반응성을 위해 즉시 숨김
                HideWindow();
                
                _isProcessing = true;
                try
                {
                    double? refStart = _settings.RefEndSec > 0 ? _settings.RefStartSec : (double?)null;
                    double? refEnd = _settings.RefEndSec > 0 ? _settings.RefEndSec : (double?)null;

                    var audioStream = await _ttsClient.GenerateAudioAsync(
                        _settings.TtsApiUrl, textToSend, _settings.SpeakerPath,
                        _settings.Alpha, _settings.Beta, _settings.DiffusionSteps,
                        _settings.Speed, refStart, refEnd);
                    if (audioStream != null)
                    {
                        await _audioService.PlayAudioAsync(audioStream, _settings.OutputDeviceName, _settings.Volume,
                            _settings.MonitorOnSend, _settings.MonitorDeviceName);
                    }
                }
                catch (Exception ex)
                {
                    // 서버 경고(예: 참조 오디오가 너무 짧음)를 앱이 죽지 않고 사용자에게 표시
                    MessageBox.Show(ex.Message, "TTS 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    _isProcessing = false;
                }
            }
        }

        // 우클릭 메뉴: 일반 창 <-> 심플 창 전환. 설정 창을 거치지 않고 바로 바꾸고 저장함.
        private void ToggleModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _settings.UseSpotlightMode = !_settings.UseSpotlightMode;
            _settings.Save();
            ApplySettings(_settings);
            InputTextBox.Focus();
        }

        private void SaveLocationButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.WindowTop = this.Top;
            _settings.WindowLeft = this.Left;
            _settings.Save();
            MessageBox.Show("현재 창 위치가 저장되었습니다! (다음 번 실행 시 여기로 켜집니다)", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 단축키는 설정에서 바뀔 수 있으므로 실제 등록된 조합을 표시
            string hideHint = _hasActiveHotkey
                ? $"Esc 또는 {HotkeyManager.Format(_activeModifiers, _activeKey)}"
                : "Esc";

            var result = MessageBox.Show(
                $"TTS Overlay를 완전히 종료하시겠습니까?\n(창을 숨기려면 {hideHint} 를 사용하세요.)",
                "종료 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                Application.Current.Shutdown();
            }
        }

        private void MainBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _hotkeyManager?.Dispose();
            _stopHotkeyManager?.Dispose();
        }
    }
}