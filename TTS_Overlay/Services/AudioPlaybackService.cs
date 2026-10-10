using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TtsOverlay.Services
{
    public class AudioPlaybackService
    {
        // 지금 재생 중인 모든 요청의 취소 토큰. 본 송출과 미리듣기는 서로 다른 인스턴스에서
        // 재생되므로, 단축키 하나로 전부 멈출 수 있게 static으로 모아 둠.
        private static readonly object _activeLock = new object();
        private static readonly List<CancellationTokenSource> _active = new List<CancellationTokenSource>();

        /// <summary>재생 중인 소리를 모두 즉시 중단(송출, 듣기, 미리듣기 전부).</summary>
        public static void StopAll()
        {
            lock (_activeLock)
            {
                foreach (var cts in _active) cts.Cancel();
            }
        }

        public List<string> GetOutputDevices()
        {
            var devices = new List<string>();
            for (int i = -1; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                devices.Add(caps.ProductName);
            }
            return devices;
        }

        /// <summary>
        /// 지정한 출력 장치로 재생. monitor가 true면 듣기 장치(monitorDeviceName, 비어 있으면 시스템 기본 장치)로도
        /// 동시에 재생해서, 가상 케이블로 송출하는 소리를 본인도 들을 수 있게 함.
        /// </summary>
        public async Task PlayAudioAsync(Stream audioStream, string targetDeviceName, float volume = 1.0f,
            bool monitor = false, string monitorDeviceName = "")
        {
            int deviceNumber = FindDevice(targetDeviceName);

            // 두 장치가 같은 WAV를 각자 읽어야 하므로 바이트로 복사해 둠
            byte[] wavBytes;
            using (var buffer = new MemoryStream())
            {
                await audioStream.CopyToAsync(buffer);
                wavBytes = buffer.ToArray();
            }

            using var cts = new CancellationTokenSource();
            lock (_activeLock) _active.Add(cts);
            try
            {
                var playbacks = new List<Task> { PlayOnDeviceAsync(wavBytes, deviceNumber, volume, cts.Token) };

                if (monitor)
                {
                    int monitorNumber = FindDevice(monitorDeviceName);
                    // 송출 장치와 듣기 장치가 같으면 이미 들리고 있으므로 한 번 더 틀지 않음(소리 겹침 방지)
                    if (!IsSameDevice(deviceNumber, monitorNumber))
                    {
                        playbacks.Add(PlayOnDeviceAsync(wavBytes, monitorNumber, volume, cts.Token));
                    }
                }

                await Task.WhenAll(playbacks);
            }
            finally
            {
                lock (_activeLock) _active.Remove(cts);
            }
        }

        /// <summary>이름으로 장치 번호를 찾음. 못 찾거나 이름이 비어 있으면 -1(시스템 기본 장치).</summary>
        private static int FindDevice(string deviceName)
        {
            // 이름이 비어 있으면 검색하지 않고 기본 장치(-1)를 그대로 씀.
            // Contains("")는 항상 참이라, 검색하면 0번 장치(가상 케이블일 수 있음)가 잡힘.
            if (string.IsNullOrEmpty(deviceName)) return -1;

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                if (WaveOut.GetCapabilities(i).ProductName.Contains(deviceName))
                    return i;
            }
            return -1;
        }

        /// <summary>두 장치 번호가 실제로 같은 장치를 가리키는지. -1(기본 장치)은 실제 기본 장치와 비교함.</summary>
        private static bool IsSameDevice(int a, int b)
        {
            if (a == b) return true;
            if (a == -1) return IsDefaultDevice(b);
            if (b == -1) return IsDefaultDevice(a);
            return false;
        }

        private static async Task PlayOnDeviceAsync(byte[] wavBytes, int deviceNumber, float volume, CancellationToken stop)
        {
            using (var waveOut = new WaveOutEvent() { DeviceNumber = deviceNumber })
            using (var reader = new WaveFileReader(new MemoryStream(wavBytes)))
            {
                // 게인을 출력 "장치"가 아니라 "샘플"에 직접 적용.
                // WaveOutEvent.Volume은 waveOutSetVolume을 호출하는데, 이건 장치 자체를 대상으로 함.
                // VB-Cable 같은 가상 장치나 상당수 드라이버가 이를 무시함. 그래서 슬라이더가 먹지 않았고,
                // (서버에서 피크 1.0으로 정규화된) 오디오가 항상 최대 음량으로 재생됐음.
                // 샘플 값을 직접 스케일하면 장치 종류와 무관하게 항상 적용됨.
                var volumeProvider = new VolumeSampleProvider(reader.ToSampleProvider())
                {
                    Volume = Math.Clamp(volume, 0f, 1f)
                };

                waveOut.Init(volumeProvider);
                waveOut.Play();

                while (waveOut.PlaybackState == PlaybackState.Playing)
                {
                    if (stop.IsCancellationRequested)
                    {
                        waveOut.Stop();
                        break;
                    }
                    await Task.Delay(30);
                }
            }
        }

        /// <summary>
        /// 해당 WaveOut 장치가 현재 Windows 기본 재생 장치인지 확인.
        /// WaveOut의 ProductName은 31자에서 잘리므로 기본 장치 이름의 앞부분과 비교함.
        /// 확인에 실패하면 false(= 다른 장치로 간주).
        /// </summary>
        private static bool IsDefaultDevice(int deviceNumber)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                string productName = WaveOut.GetCapabilities(deviceNumber).ProductName;
                return defaultDevice.FriendlyName.StartsWith(productName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
