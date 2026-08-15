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
        [DllImport("__Internal")] static extern IntPtr il2cpplab_gpu_stats_sink();
        [DllImport("__Internal")] static extern void il2cpplab_gpu_pass_meta(uint[] words, uint count);
        [DllImport("__Internal")] static extern uint perflab_marker_register(string name);
        [DllImport("__Internal")] static extern void il2cpplab_gpu_announce(uint flags);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_gpu_probe_set_sink(IntPtr sink);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_gpu_probe_set_stats_sink(IntPtr sink);
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
            uint site = SiteOf(pass);
            EmitPassMeta(site, pass);
            cmd.IssuePluginEventAndData(eventFunc, EventPassBegin, Pack(site, frame));
        }

        // A pass target's shape (dims/format/MSAA/depth), once per site when first seen
        // or changed — the bandwidth context behind the pass's GPU time. Cleared when a
        // capture (re)starts so every session carries its own copy.
        static readonly uint[] metaWords = new uint[6];
        static readonly Dictionary<uint, ulong> metaBySite = new Dictionary<uint, ulong>(64);

        static void EmitPassMeta(uint site, ScriptableRenderPass pass)
        {
            if (site == 0)
                return;
            var handles = pass.colorAttachmentHandles;
            var rt = handles != null && handles.Length > 0 ? handles[0]?.rt : null;
            if (rt == null)
                return; // backbuffer / camera-managed target: no descriptor to read
            var d = rt.descriptor;
            ulong packed = ((ulong)(uint)d.width << 42) ^ ((ulong)(uint)d.height << 20)
                         ^ ((ulong)(uint)d.graphicsFormat << 6) ^ (uint)d.msaaSamples;
            if (metaBySite.TryGetValue(site, out ulong prev) && prev == packed)
                return;
            metaBySite[site] = packed;
            metaWords[0] = site;
            metaWords[1] = (uint)d.width;
            metaWords[2] = (uint)d.height;
            metaWords[3] = (uint)d.graphicsFormat;
            metaWords[4] = (uint)d.msaaSamples;
            metaWords[5] = (uint)d.depthBufferBits;
            il2cpplab_gpu_pass_meta(metaWords, 6);
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
                if (spansOn)
                    metaBySite.Clear(); // a capture (re)started: re-emit every pass's meta
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
                // per-pass pipeline statistics (D3D11 backend; the probe no-ops elsewhere)
                il2cpplab_gpu_probe_set_stats_sink(il2cpplab_gpu_stats_sink());
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
