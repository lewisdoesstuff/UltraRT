using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>
    /// Wet blood (docs/vfx.md, "Wet blood"). The game keeps its blood stains in a ring buffer on the GPU
    /// (BloodsplatterManager: up to 100,000 stains, each a slot; the newest overwrite the oldest) and stores no age. A
    /// Harmony postfix on CreateBloodstain records the birth time of every slot here; while any stain is younger than the
    /// drying time, <see cref="Render"/> draws those stains (only those: they are the last slots written) into a
    /// screen-sized mask of wetness with UltraRT/BloodWet, a port of the game's stain shader. PathTrace reads the mask at
    /// each primary hit and makes the surface under a wet stain glossy and darker; the reflection ray and the denoisers do
    /// the rest. The game still draws the stain itself (its multiplicative composite), on top of the ray-traced image.
    /// Costs nothing while no blood is fresh.
    /// </summary>
    internal sealed class BloodWet : IDisposable
    {
        public static BloodWet Instance;

        private readonly ManualLogSource _log;
        private readonly Material _mat;
        private float[] _birth = new float[0];
        private ComputeBuffer _birthBuffer, _args;
        private readonly uint[] _argData = new uint[5];
        private RenderTexture _mask;
        private BloodsplatterManager _owner;
        private int _newest = -1;          // the slot written last
        private int _dirtyMin = int.MaxValue, _dirtyMax = -1;
        private float _lastBirth = -1e9f;
        private int _logged;
        private readonly RenderTargetIdentifier[] _cameraTargets = new RenderTargetIdentifier[3];

        public RenderTexture Mask => _mask;
        public int FreshStains { get; private set; }
        public static bool Hooked;
        public static string HookError;

        private static readonly int
            IdInstance = Shader.PropertyToID("instanceBuffer"), IdParent = Shader.PropertyToID("parentBuffer"), IdBirth = Shader.PropertyToID("_BloodBirth"),
            IdAtlas = Shader.PropertyToID("_SplatterAtlas"), IdDepth = Shader.PropertyToID("_BloodDepth"), IdNormal = Shader.PropertyToID("_BloodNormal"),
            IdVP = Shader.PropertyToID("_BloodVP"), IdInvVP = Shader.PropertyToID("_BloodInvVP"), IdParams = Shader.PropertyToID("_BloodParams"),
            IdParams2 = Shader.PropertyToID("_BloodParams2"), IdScreen = Shader.PropertyToID("_BloodScreen");

        public BloodWet(ManualLogSource log, Shader shader)
        {
            _log = log;
            _mat = new Material(shader) { name = "UltraRT BloodWet", hideFlags = HideFlags.HideAndDontSave };
            Instance = this;
        }

        /// <summary>Observe only: the slot the game just wrote a stain into.</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                var create = AccessTools.Method(typeof(BloodsplatterManager), "CreateBloodstain",
                    new[] { typeof(Vector3), typeof(Vector3), typeof(bool), typeof(BloodstainParent) });
                if (create == null) { HookError = "BloodsplatterManager.CreateBloodstain not found"; return; }
                harmony.Patch(create, postfix: new HarmonyMethod(typeof(BloodWet).GetMethod(nameof(CreatePostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                Hooked = true;
                // A checkpoint reload puts older stains back into the slots: none of them is fresh.
                var load = AccessTools.Method(typeof(BloodsplatterManager), "LoadBloodstains");
                if (load != null) harmony.Patch(load, postfix: new HarmonyMethod(typeof(BloodWet).GetMethod(nameof(LoadPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { HookError = e.Message; }
        }

        private static void CreatePostfix(BloodsplatterManager __instance, int __result)
        {
            var i = Instance;
            if (i != null) i.OnStain(__instance, __result);
        }

        private static void LoadPostfix() { Instance?.Forget(); }

        private void OnStain(BloodsplatterManager bsm, int slot)
        {
            if (!ReferenceEquals(bsm, _owner)) Adopt(bsm);
            if ((uint)slot >= (uint)_birth.Length) return;
            float now = Time.time;
            _birth[slot] = now;
            _newest = slot;
            _lastBirth = now;
            if (slot < _dirtyMin) _dirtyMin = slot;
            if (slot > _dirtyMax) _dirtyMax = slot;
        }

        private void Adopt(BloodsplatterManager bsm)
        {
            if (_owner != null) _owner.StainsCleared -= Forget;
            _owner = bsm;
            int n = bsm != null && bsm.props.IsCreated ? bsm.props.Length : 0;
            _birth = new float[n];
            Forget();
            _birthBuffer?.Release();
            _birthBuffer = n > 0 ? new ComputeBuffer(n, 4) { name = "UltraRT Blood Birth" } : null;
            _birthBuffer?.SetData(_birth);
            if (bsm != null) bsm.StainsCleared += Forget;
        }

        /// <summary>Every stain is dry (stains cleared, checkpoint reloaded, new level).</summary>
        public void Forget()
        {
            for (int i = 0; i < _birth.Length; i++) _birth[i] = -1e9f;
            _newest = -1;
            _lastBirth = -1e9f;
            _dirtyMin = 0;
            _dirtyMax = _birth.Length - 1;
            FreshStains = 0;
        }

        /// <summary>
        /// Draws the wetness mask for this frame into <paramref name="cmd"/> and puts the camera's targets back. Returns
        /// false (nothing recorded, no mask) when no stain is wet or the game's stain buffers aren't there.
        /// </summary>
        public bool Render(CommandBuffer cmd, Camera cam, WetSettings s)
        {
            FreshStains = 0;
            var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
            var bsm = _owner;
            float now = Time.time;
            float dry = Mathf.Max(0.1f, s.bloodDryTime);
            if (bsm == null || pp == null || _newest < 0 || now - _lastBirth > dry || _lastBirth > now + 0.5f) return false;
            if (!bsm.usedComputeShadersAtStart || bsm.instanceBuffer == null || bsm.parentBuffer == null || bsm.optimizedBloodMesh == null ||
                bsm.stainMat == null || _birthBuffer == null) return false;
            var main = pp.mainTex;
            var depth = pp.depthBuffer;
            if (main == null || depth == null || pp.viewNormal == null || pp.reusableBufferA == null) return false;

            // The wet stains are the newest slots: walk back from the last one written until a dry one.
            int cap = _birth.Length, count = 0, max = Mathf.Min(cap, Mathf.Max(16, s.bloodMaxStains));
            for (int i = _newest; count < max; i = i == 0 ? cap - 1 : i - 1)
            {
                float age = now - _birth[i];
                if (age > dry || age < -0.5f) break;
                count++;
            }
            if (count == 0) return false;
            FreshStains = count;
            int first = ((_newest - count + 1) % cap + cap) % cap;

            if (_dirtyMax >= _dirtyMin)
            {
                int a = Mathf.Clamp(_dirtyMin, 0, cap - 1), b = Mathf.Clamp(_dirtyMax, 0, cap - 1);
                _birthBuffer.SetData(_birth, a, a, b - a + 1);
                _dirtyMin = int.MaxValue;
                _dirtyMax = -1;
            }
            if (_mask == null || _mask.width != main.width || _mask.height != main.height || !_mask.IsCreated())
            {
                if (_mask != null) { _mask.Release(); Object.Destroy(_mask); }
                _mask = new RenderTexture(main.width, main.height, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
                    { name = "UltraRT Blood Wetness", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                _mask.Create();
            }
            var mesh = bsm.optimizedBloodMesh;
            if (_args == null) _args = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments) { name = "UltraRT Blood Args" };
            _argData[0] = mesh.GetIndexCount(0);
            _argData[1] = (uint)count;
            _argData[2] = mesh.GetIndexStart(0);
            _argData[3] = mesh.GetBaseVertex(0);
            _argData[4] = 0;
            _args.SetData(_argData);

            var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            var m = _mat;
            m.SetBuffer(IdInstance, bsm.instanceBuffer);
            m.SetBuffer(IdParent, bsm.parentBuffer);
            m.SetBuffer(IdBirth, _birthBuffer);
            var atlas = bsm.stainMat.HasProperty(IdAtlas) ? bsm.stainMat.GetTexture(IdAtlas) : null;
            m.SetTexture(IdAtlas, atlas != null ? atlas : Texture2D.whiteTexture);
            m.SetMatrix(IdVP, vp);
            m.SetMatrix(IdInvVP, vp.inverse);
            m.SetVector(IdParams, new Vector4(first, cap, now, 0f));
            m.SetVector(IdParams2, new Vector4(Mathf.Max(0f, s.bloodWetTime), dry, Shader.GetGlobalFloat("_StainWarping"), Shader.GetGlobalFloat("_TextureWarping")));
            m.SetVector(IdScreen, new Vector4(1f / main.width, 1f / main.height, 0, 0));
            cmd.SetGlobalTexture(IdDepth, depth, RenderTextureSubElement.Depth);
            cmd.SetGlobalTexture(IdNormal, pp.viewNormal);
            cmd.SetRenderTarget(_mask);
            cmd.ClearRenderTarget(false, true, Color.clear);
            cmd.DrawMeshInstancedIndirect(mesh, 0, m, 0, _args, 0);
            // Back to the camera's own targets (colour, outline buffer, view normals + depth): the RT composite draws next.
            _cameraTargets[0] = main;
            _cameraTargets[1] = pp.reusableBufferA;
            _cameraTargets[2] = pp.viewNormal;
            cmd.SetRenderTarget(_cameraTargets, depth);

            if (_logged < 6 && Time.frameCount % 60 == 0)
            {
                _logged++;
                _log.LogInfo($"Wet blood: {count} fresh stains (slots {first}..{_newest} of {cap}), atlas {(atlas != null ? atlas.name : "MISSING")}, mesh '{mesh.name}' {mesh.GetIndexCount(0)} indices");
            }
            return true;
        }

        public void Dispose()
        {
            if (_owner != null) _owner.StainsCleared -= Forget;
            _owner = null;
            _birthBuffer?.Release();
            _args?.Release();
            if (_mask != null) { _mask.Release(); Object.Destroy(_mask); _mask = null; }
            if (_mat != null) Object.Destroy(_mat);
            if (ReferenceEquals(Instance, this)) Instance = null;
        }
    }
}
