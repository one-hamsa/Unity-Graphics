// il2cpplab gpu-tracking builds only (IL2CPPLAB_GPU): everything here compiles away in
// every other configuration, editor included.
#if IL2CPPLAB_CAPTURE && IL2CPPLAB_GPU && !UNITY_EDITOR && (UNITY_ANDROID || UNITY_STANDALONE_WIN)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// gpulab tier B: brackets every ScriptableRenderPass with GPU timestamp events
    /// handled by the il2cpplab_gpu_probe native plugin, which forwards per-pass GPU
    /// durations to the il2cpplab recorder (per-pass spans on the capture timeline).
    /// Called from ScriptableRenderer.ExecuteRenderPass; self-initializes on the first
    /// call, gates itself once per frame on il2cpplab_gpu_control() (`gpu_spans` in
    /// control.txt), and issues zero events while no capture is active.
    /// Pass identity is the pass's profilingSampler name, interned through the perflab
    /// marker table so the parser names spans for free.
    /// </summary>
    internal static class GpuLabUrpHook
    {
        [DllImport("__Internal")] static extern uint il2cpplab_gpu_control();
        [DllImport("__Internal")] static extern IntPtr il2cpplab_gpu_span_sink();
        [DllImport("__Internal")] static extern uint perflab_marker_register(string name);
        [DllImport("__Internal")] static extern void il2cpplab_gpu_announce(uint flags);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_gpu_probe_set_sink(IntPtr sink);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_gpu_probe_set_enabled(int enabled);
        [DllImport("il2cpplab_gpu_probe")] static extern IntPtr il2cpplab_gpu_probe_event_func();

        // event ids — must match gpulab_probe.cpp
        const int EventFrameSetup = 1;
        const int EventPassBegin = 2;
        const int EventPassEnd = 3;

        static bool initTried;
        static IntPtr eventFunc;
        static readonly Dictionary<ProfilingSampler, uint> siteBySampler =
            new Dictionary<ProfilingSampler, uint>(64);
        static int lastGateFrame = -1;
        static bool frameActive;
        static bool setupPending;
        static int pluginEnabled = -1; // last value pushed to the plugin; -1 = never

        public static void BeginPass(CommandBuffer cmd, ScriptableRenderPass pass)
        {
            int frame = Time.frameCount;
            if (frame != lastGateFrame)
                GateFrame(frame);
            if (!frameActive)
                return;
            if (setupPending)
            {
                setupPending = false;
                cmd.IssuePluginEventAndData(eventFunc, EventFrameSetup, Pack(0, frame));
            }
            cmd.IssuePluginEventAndData(eventFunc, EventPassBegin, Pack(SiteOf(pass), frame));
        }

        public static void EndPass(CommandBuffer cmd, ScriptableRenderPass pass)
        {
            if (!frameActive)
                return;
            cmd.IssuePluginEventAndData(eventFunc, EventPassEnd, Pack(SiteOf(pass), lastGateFrame));
        }

        static void GateFrame(int frame)
        {
            lastGateFrame = frame;
            bool spansOn = (il2cpplab_gpu_control() & 0x2) != 0;
            if (spansOn && !initTried)
                Init();
            if (eventFunc == IntPtr.Zero)
            {
                frameActive = false;
                return;
            }
            int enabled = spansOn ? 1 : 0;
            if (enabled != pluginEnabled)
            {
                pluginEnabled = enabled;
                il2cpplab_gpu_probe_set_enabled(enabled);
            }
            frameActive = spansOn;
            setupPending = spansOn;
        }

        static void Init()
        {
            initTried = true;
            try
            {
                eventFunc = il2cpplab_gpu_probe_event_func();
                il2cpplab_gpu_probe_set_sink(il2cpplab_gpu_span_sink());
                il2cpplab_gpu_announce(0x2); // session_header.gpu_flags bit 1: pass spans
                Debug.Log("[il2cpplab] gpu pass-span probe connected");
            }
            catch (DllNotFoundException e)
            {
                eventFunc = IntPtr.Zero;
                Debug.LogError("[il2cpplab] il2cpplab_gpu_probe plugin missing — per-pass GPU spans unavailable");
                Debug.LogException(e);
            }
        }

        static uint SiteOf(ScriptableRenderPass pass)
        {
            var sampler = pass.profilingSampler;
            if (sampler == null)
                return 0; // recorder treats site 0 as unattributed
            if (!siteBySampler.TryGetValue(sampler, out uint site))
            {
                // 0 when the marker table is full: those spans stay unattributed rather
                // than mis-attributed
                site = perflab_marker_register(sampler.name);
                siteBySampler.Add(sampler, site);
            }
            return site;
        }

        // the probe decodes data as (site << 32 | frame_index)
        static IntPtr Pack(uint site, int frame)
        {
            return (IntPtr)(long)(((ulong)site << 32) | (uint)frame);
        }
    }
}
#endif
