using System;
using System.Runtime.InteropServices;

namespace Capsule
{
    internal interface ISystemVolume
    {
        bool TryRead(out double level, out bool muted);
        bool TrySetLevel(double level);
        bool TryToggleMute(out bool muted);
    }

    internal sealed class SystemVolume : ISystemVolume
    {
        const uint ClsctxInprocServer = 1;
        static readonly Guid EndpointVolumeId = typeof(IAudioEndpointVolume).GUID;

        public bool TryRead(out double level, out bool muted)
        {
            float scalar = 0;
            bool isMuted = false;
            bool available = TryUseEndpoint(endpoint =>
                endpoint.GetMasterVolumeLevelScalar(out scalar) >= 0 && endpoint.GetMute(out isMuted) >= 0);
            level = scalar * 100.0;
            muted = isMuted;
            return available && scalar >= 0 && scalar <= 1;
        }

        public bool TrySetLevel(double level)
        {
            return TryUseEndpoint(endpoint => endpoint.SetMasterVolumeLevelScalar((float)(level / 100), IntPtr.Zero) >= 0);
        }

        public bool TryToggleMute(out bool muted)
        {
            bool newMute = false;
            bool changed = TryUseEndpoint(endpoint =>
            {
                bool current;
                if (endpoint.GetMute(out current) < 0) return false;
                newMute = !current;
                return endpoint.SetMute(newMute, IntPtr.Zero) >= 0;
            });
            muted = newMute;
            return changed;
        }

        // Resolve the current default on each operation so a device switch needs no callback or cached COM state.
        static bool TryUseEndpoint(Func<IAudioEndpointVolume, bool> use)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            object activation = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) < 0) return false;
                Guid iid = EndpointVolumeId;
                if (device.Activate(ref iid, ClsctxInprocServer, IntPtr.Zero, out activation) < 0) return false;
                var endpoint = activation as IAudioEndpointVolume;
                return endpoint != null && use(endpoint);
            }
            catch (COMException) { return false; }
            finally
            {
                if (activation != null) Marshal.ReleaseComObject(activation);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        enum EDataFlow { Render = 0 }
        enum ERole { Multimedia = 1 }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, uint stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        // Keep the SDK's vtable order, including unused slots before GetMute.
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float decibels, IntPtr eventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float decibels);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, IntPtr eventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        }
    }
}
