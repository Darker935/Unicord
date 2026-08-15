using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Unicord.Universal.Voice;

namespace Unicord.Universal.Models
{
    public class DeviceInformationWrapper
    {
        public DeviceInformation Info { get; set; }

        public string Id => Info?.Id;
        public string Name => Info?.Name;

        public static implicit operator DeviceInformationWrapper(DeviceInformation info) { return new DeviceInformationWrapper() { Info = info }; }
    }

    public class VoiceSettingsModel : ViewModelBase
    {
        private DeviceInformationWrapper _inputDevice;
        private DeviceInformationWrapper _outputDevice;

        public VoiceSettingsModel()
        {
            AvailableInputDevices = new List<DeviceInformationWrapper>();
            AvailableOutputDevices = new List<DeviceInformationWrapper>();
            AvailableInputDevices.Add(new DeviceInformationWrapper());
            AvailableOutputDevices.Add(new DeviceInformationWrapper());
        }

        public async Task LoadAsync()
        {
            foreach (var dev in await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture))
            {
                AvailableInputDevices.Add(dev);
            }

            foreach (var dev in await DeviceInformation.FindAllAsync(DeviceClass.AudioRender))
            {
                AvailableOutputDevices.Add(dev);
            }

            var inputDeviceId = App.LocalSettings.Read<string>("InputDevice", null);
            var outputDeviceId = App.LocalSettings.Read<string>("OutputDevice", null);

            _inputDevice = AvailableInputDevices.FirstOrDefault(d => d?.Id == inputDeviceId);
            _outputDevice = AvailableOutputDevices.FirstOrDefault(d => d?.Id == outputDeviceId);

            InvokePropertyChanged(nameof(AvailableInputDevices));
            InvokePropertyChanged(nameof(AvailableOutputDevices));
            InvokePropertyChanged(nameof(InputDevice));
            InvokePropertyChanged(nameof(OutputDevice));
        }

        internal Task SaveAsync()
        {
            App.LocalSettings.Save("InputDevice", InputDevice?.Id);
            App.LocalSettings.Save("OutputDevice", OutputDevice?.Id);

            return Task.CompletedTask;
        }

        public List<DeviceInformationWrapper> AvailableInputDevices { get; set; }
        public List<DeviceInformationWrapper> AvailableOutputDevices { get; set; }

        public DeviceInformationWrapper InputDevice
        {
            get => _inputDevice;
            set => OnPropertySet(ref _inputDevice, value);
        }

        public DeviceInformationWrapper OutputDevice
        {
            get => _outputDevice;
            set => OnPropertySet(ref _outputDevice, value);
        }

        public uint SuppressionLevel
        {
            get => App.LocalSettings.Read("NoiseSuppression", (uint)NoiseSuppressionLevel.Medium);
            set => App.LocalSettings.Save("NoiseSuppression", value);
        }

        public bool VoiceActivity
        {
            get => App.LocalSettings.Read("VoiceActivity", true);
            set => App.LocalSettings.Save("VoiceActivity", value);
        }

        public bool EchoCancellation
        {
            get => App.LocalSettings.Read("EchoCancellation", true);
            set => App.LocalSettings.Save("EchoCancellation", value);
        }

        public bool AutomaticGainControl
        {
            get => App.LocalSettings.Read("AutomaticGainControl", true);
            set => App.LocalSettings.Save("AutomaticGainControl", value);
        }

        /// <summary>
        /// 0 = Voice (VOIP, DTX, inband FEC), 1 = Music (full-band stereo, continuous).
        /// </summary>
        public int AudioMode
        {
            get => (int)App.LocalSettings.Read(VoiceQualityOptions.ModeSetting, (uint)VoiceAudioMode.Voice);
            set
            {
                App.LocalSettings.Save(VoiceQualityOptions.ModeSetting, (uint)Math.Max(0, value));
                InvokePropertyChanged(nameof(AudioMode));
            }
        }

        public double MinBitrateKbps
        {
            get => App.LocalSettings.Read(VoiceQualityOptions.MinBitrateSetting, (uint)VoiceQualityOptions.DefaultMinBitrate) / 1000d;
            set
            {
                var kbps = (uint)Math.Max(1, value);
                App.LocalSettings.Save(VoiceQualityOptions.MinBitrateSetting, kbps * 1000);
                if (kbps > MaxBitrateKbps)
                    MaxBitrateKbps = kbps;
                InvokePropertyChanged(nameof(MinBitrateKbps));
            }
        }

        public double MaxBitrateKbps
        {
            get => App.LocalSettings.Read(VoiceQualityOptions.MaxBitrateSetting, (uint)VoiceQualityOptions.DefaultMaxBitrate) / 1000d;
            set
            {
                var kbps = (uint)Math.Max(1, value);
                App.LocalSettings.Save(VoiceQualityOptions.MaxBitrateSetting, kbps * 1000);
                InvokePropertyChanged(nameof(MaxBitrateKbps));
            }
        }

        /// <summary>
        /// Records the per-window audio metrics to the trace. Off by default: they are
        /// several hundred bytes every five seconds plus the work to format them, which is
        /// worth paying only while a fault is being chased.
        /// </summary>
        public bool DiagnosticLogging
        {
            get => DiagnosticLog.Voice.IsEnabled;
            set
            {
                DiagnosticLog.Voice.SetEnabled(value);
                InvokePropertyChanged(nameof(DiagnosticLogging));
            }
        }

        public double MinSupportedKbps => VoiceQualityOptions.MinSupportedBitrate / 1000d;
        public double MaxSupportedKbps => VoiceQualityOptions.MaxSupportedBitrate / 1000d;

        public bool AdaptiveBitrate
        {
            get => App.LocalSettings.Read(VoiceQualityOptions.AdaptiveSetting, true);
            set => App.LocalSettings.Save(VoiceQualityOptions.AdaptiveSetting, value);
        }

        public bool ForwardErrorCorrection
        {
            get => App.LocalSettings.Read(VoiceQualityOptions.FecSetting, true);
            set => App.LocalSettings.Save(VoiceQualityOptions.FecSetting, value);
        }

        /// <summary>
        /// 0 = Auto (Opus decides), 1 = Prefer stereo (kept while the bitrate allows),
        /// 2 = Always stereo. Ignored in Music mode, which is always stereo.
        /// </summary>
        public int StereoMode
        {
            get => (int)App.LocalSettings.Read(VoiceQualityOptions.StereoSetting, (uint)StereoPreference.Auto);
            set
            {
                App.LocalSettings.Save(VoiceQualityOptions.StereoSetting, (uint)Math.Max(0, value));
                InvokePropertyChanged(nameof(StereoMode));
            }
        }
    }
}
