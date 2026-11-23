using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// The item V1 holds (a skull, a torch, a soap bar) in its third-person left hand, for reflections (docs/reflections.md,
    /// "The held item in V1's hand"). The game shows a held item only on the first-person viewmodel: FistControl's
    /// ResetHeldItemPosition puts every object of it on layer 13 under the viewmodel arm (Punch.holder), and the third-person
    /// model (PlayerAnimations) has no attachment for it, so V1 held nothing in its reflections while the viewmodel showed a
    /// glowing skull (user, playtest of 2026-11-21, 8-3's supermarket glass). The item's own proxy leaves the RTAS while it
    /// is on layer 13 (HeldHidden).
    ///
    /// While an item is held, a copy of its mesh renderers hangs under the left-hand bone of the third-person model, with
    /// the item's own materials (the plain proxy materials, as the item has on the ground: a skull's emission stays) and
    /// the player mask, so it shows wherever V1 does (reflections, bounces, shadows; never camera rays). It has the size the
    /// item has on the ground, and the rotation the viewmodel gives it. The copies are never drawn by the game
    /// (forceRenderingOff) and leave the RTAS when the item is put down, thrown or dropped.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>Show the held item in V1's third-person left hand (Plugin's _v1HeldItem, bench plugin._v1HeldItem=0 = as
        /// before 2026-11-21, -ultrart-v1-noheld).</summary>
        public static bool PlayerHeldItem = true;

        /// <summary>The held copy's centre, from the hand bone along the hand's axes (m), for the palm.</summary>
        public static Vector3 PlayerHeldOffset = Vector3.zero;

        /// <summary>The held copy's largest side in the world (m; Plugin's _v1HeldSize, -ultrart-v1-held-size). The item's
        /// own size on the ground (a skull is ~1.7 m across there, scaled up for the first-person view) is far too big for
        /// V1's hand; 0 = that ground size.</summary>
        public static float PlayerHeldSize = 0.35f;

        private Transform _playerLeftHand;

        private sealed class HeldMirror
        {
            public ItemIdentifier Item;
            public GameObject Root;
            public readonly List<(Renderer Src, MeshRenderer Copy)> Parts = new List<(Renderer, MeshRenderer)>();
            /// <summary>The item's renderers' bounds centre in the item's local space (the copy is centred on the hand).</summary>
            public Vector3 CentreLocal;
            /// <summary>The largest side of the visible parts' bounds in the item's local space (unscaled).</summary>
            public float SizeLocal;
        }

        private readonly Dictionary<ItemIdentifier, HeldMirror> _heldMirrors = new Dictionary<ItemIdentifier, HeldMirror>();
        /// <summary>World scale of an item while it is not held (seen at the scene build and after it is put down).</summary>
        private readonly Dictionary<ItemIdentifier, Vector3> _itemGroundScale = new Dictionary<ItemIdentifier, Vector3>();
        private ItemIdentifier _heldItem;
        private HeldMirror _heldShown;
        private int _heldLogs2;
        private readonly List<Material> _heldSrcMats = new List<Material>(4);
        private readonly List<Material> _heldCopyMats = new List<Material>(4);
        private readonly MaterialPropertyBlock _heldBlock = new MaterialPropertyBlock();

        /// <summary>Scene build: the third-person left hand (PlayerAnimations.leftArm, then its "hand" child) and the items'
        /// world scale on the ground.</summary>
        private void FindPlayerHand(PlayerAnimations anims)
        {
            _playerLeftHand = null;
            _heldMirrors.Clear();
            _itemGroundScale.Clear();
            _heldItem = null;
            _heldShown = null;
            if (anims == null) return;
            Transform arm = null;
            try { arm = HarmonyLib.AccessTools.Field(typeof(PlayerAnimations), "leftArm")?.GetValue(anims) as Transform; }
            catch (System.Exception e) { _log.LogWarning($"V1 held item: no PlayerAnimations.leftArm: {e.Message}"); }
            if (arm == null) { _log.LogInfo("V1 held item: no left arm on the third-person model; held items are not shown"); return; }
            // The shallowest descendant named like a hand, else the end of the arm's first-child chain.
            Transform hand = null;
            var queue = new Queue<Transform>();
            queue.Enqueue(arm);
            while (queue.Count > 0 && hand == null)
            {
                var t = queue.Dequeue();
                if (t != arm && t.name.IndexOf("hand", System.StringComparison.OrdinalIgnoreCase) >= 0) { hand = t; break; }
                for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
            }
            if (hand == null)
            {
                hand = arm;
                while (hand.childCount > 0 && hand.GetChild(0).name.IndexOf("[UltraRT]", System.StringComparison.Ordinal) < 0) hand = hand.GetChild(0);
            }
            _playerLeftHand = hand;
            var chain = new List<string>();
            for (var t = arm; t != null && chain.Count < 8; t = t.childCount > 0 ? t.GetChild(0) : null) chain.Add(t.name);
            _log.LogInfo($"V1 held item: left arm '{GameControlPath(arm)}' (chain {string.Join(" > ", chain)}), hand '{hand.name}' scale {hand.lossyScale:F3}");
            foreach (var it in Object.FindObjectsByType<ItemIdentifier>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (it != null && it.gameObject.layer != 13) _itemGroundScale[it] = it.transform.lossyScale;
        }

        /// <summary>Per frame (Plugin, after SyncDynamic, with the player glow): the held item's copy in V1's hand.</summary>
        public void UpdateHeldItem()
        {
            ItemIdentifier held = null;
            if (PlayerHeldItem && _playerLeftHand != null)
            {
                var fc = MonoSingleton.GetInstance(typeof(FistControl)) as FistControl; // never creates one
                if (fc != null && fc.heldObject != null) held = fc.heldObject;
            }
            if (!ReferenceEquals(held, _heldItem))
            {
                // The item that was held is back in the world: its scale there is the one to use next time.
                if (_heldItem != null && _heldItem.gameObject.layer != 13) _itemGroundScale[_heldItem] = _heldItem.transform.lossyScale;
                if (_heldShown != null && _heldShown.Root != null) _heldShown.Root.SetActive(false);
                _heldShown = null;
                _heldItem = held;
                if (held != null)
                {
                    if (!_heldMirrors.TryGetValue(held, out var m) || m.Root == null) _heldMirrors[held] = m = BuildHeldMirror(held);
                    _heldShown = m;
                    if (m != null && m.Parts.Count > 0) m.Root.SetActive(true);
                }
            }
            if (_heldShown != null) PlaceHeldMirror(_heldShown);
        }

        private HeldMirror BuildHeldMirror(ItemIdentifier item)
        {
            var it = item.transform;
            var m = new HeldMirror { Item = item };
            var root = new GameObject($"{item.name} in V1's hand") { hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable };
            root.SetActive(false);
            root.layer = _playerLeftHand.gameObject.layer;
            root.transform.SetParent(_playerLeftHand, false);
            _proxyObjects.Add(root);
            m.Root = root;
            Bounds? box = null;
            var notes = new List<string>();
            foreach (var src in item.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (src == null || IsProxyObject(src.gameObject) || src.name.EndsWith("[UltraRT]", System.StringComparison.Ordinal)) continue;
                var mesh = src.GetComponent<MeshFilter>()?.sharedMesh;
                var mats = src.sharedMaterials;
                if (mesh == null || !AnyTraceable(mats)) { notes.Add($"'{src.name}' skipped ({(mesh == null ? "no mesh" : "not traced")})"); continue; }
                var go = new GameObject(src.name) { hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable };
                go.layer = root.layer;
                go.transform.SetParent(root.transform, false);
                // Where the part sits in the item, in the item's own space.
                go.transform.localPosition = it.InverseTransformPoint(src.transform.position);
                go.transform.localRotation = Quaternion.Inverse(it.rotation) * src.transform.rotation;
                var ls = it.lossyScale;
                var ps = src.transform.lossyScale;
                go.transform.localScale = new Vector3(SafeDiv(ps.x, ls.x), SafeDiv(ps.y, ls.y), SafeDiv(ps.z, ls.z));
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.SetActive(src.enabled && src.gameObject.activeInHierarchy);
                var copy = go.AddComponent<MeshRenderer>();
                copy.sharedMaterials = mats;
                copy.shadowCastingMode = ShadowCastingMode.Off;
                copy.receiveShadows = false;
                copy.forceRenderingOff = true; // the game never draws it: it is only in the RTAS
                _known.Add(copy); // rescans skip it: registered here, with the player mask
                // The item's own proxy materials (not V1's lit variants): it looks as it does on the ground.
                var proxyMats = mats.Select(ProxyOrDisabled).ToArray();
                var proxy = CreateMeshProxy(go.name + " [UltraRT]", go.transform, mesh, proxyMats);
                AddDynamic(copy, proxy, SubmeshFlags(mats, mesh.subMeshCount), mats, PlayerMask);
                var d = _dynamic[_dynamic.Count - 1];
                TrackSync(d, mats, mesh);
                _playerProxies.Add(d);
                m.Parts.Add((src, copy));
                // The visible variant's bounds (a skull has a part per season, one active) in the item's space.
                if (src.enabled && src.gameObject.activeInHierarchy)
                {
                    var lb = mesh.bounds;
                    for (int k = 0; k < 8; k++)
                    {
                        var corner = lb.center + Vector3.Scale(lb.extents, new Vector3((k & 1) != 0 ? 1 : -1, (k & 2) != 0 ? 1 : -1, (k & 4) != 0 ? 1 : -1));
                        var c = it.InverseTransformPoint(src.transform.TransformPoint(corner));
                        if (box.HasValue) { var b = box.Value; b.Encapsulate(c); box = b; } else box = new Bounds(c, Vector3.zero);
                    }
                }
                notes.Add($"'{src.name}' [{string.Join(", ", mats.Select(x => x != null ? x.name : "null"))}]");
            }
            m.CentreLocal = box.HasValue ? box.Value.center : Vector3.zero;
            m.SizeLocal = box.HasValue ? Mathf.Max(box.Value.size.x, Mathf.Max(box.Value.size.y, box.Value.size.z)) : 0f;
            bool known = _itemGroundScale.TryGetValue(item, out var gs);
            _log.LogInfo($"V1 held item: '{GameControlPath(it)}' ({item.itemType}) {m.Parts.Count} part(s) in the hand: {string.Join("; ", notes)}; " +
                         $"held scale {it.lossyScale:F3}, ground scale {(known ? gs.ToString("F3") : "unknown")}, centre {m.CentreLocal:F3}, " +
                         $"size {m.SizeLocal:F3} local = {m.SizeLocal * (known ? gs.x : it.lossyScale.x):F2} m on the ground, {HeldScale(m, Vector3.one).x * m.SizeLocal:F2} m in V1's hand");
            if (m.Parts.Count == 0) { root.SetActive(false); }
            return m;
        }

        private static float SafeDiv(float a, float b) => Mathf.Abs(b) > 1e-6f ? a / b : a;

        /// <summary>The copy's world scale: uniform, for PlayerHeldSize metres across, else the item's ground scale.</summary>
        private Vector3 HeldScale(HeldMirror m, Vector3 fallback)
        {
            if (PlayerHeldSize > 0f && m.SizeLocal > 1e-4f) return Vector3.one * (PlayerHeldSize / m.SizeLocal);
            return m.Item != null && _itemGroundScale.TryGetValue(m.Item, out var gs) ? gs : fallback;
        }

        /// <summary>The copy follows the hand, turned as the viewmodel turns the item, at PlayerHeldSize (else the item's ground size); its
        /// materials and property blocks follow the item's.</summary>
        private void PlaceHeldMirror(HeldMirror m)
        {
            if (m.Root == null || m.Item == null || _playerLeftHand == null) return;
            var it = m.Item.transform;
            var rt = m.Root.transform;
            Vector3 scale = HeldScale(m, it.lossyScale);
            var hs = _playerLeftHand.lossyScale;
            var local = new Vector3(SafeDiv(scale.x, hs.x), SafeDiv(scale.y, hs.y), SafeDiv(scale.z, hs.z));
            if (rt.localScale != local) rt.localScale = local;
            var rot = it.rotation;
            var palm = _playerLeftHand.TransformPoint(PlayerHeldOffset);
            var pos = palm - rot * Vector3.Scale(m.CentreLocal, scale);
            if (rt.rotation != rot) rt.rotation = rot;
            if (rt.position != pos) rt.position = pos;
            // Runtime material changes of the item (a skull's glow is set on its material or in a block).
            foreach (var (src, copy) in m.Parts)
            {
                if (src == null || copy == null) continue;
                // Only the parts the item shows: a skull carries its seasonal variants (Pumpkin, Christmas, Easter,
                // NewSkull) as children, one of them active.
                bool on = src.enabled && src.gameObject.activeInHierarchy;
                if (copy.gameObject.activeSelf != on) copy.gameObject.SetActive(on);
                if (!on) continue;
                src.GetSharedMaterials(_heldSrcMats);
                copy.GetSharedMaterials(_heldCopyMats);
                bool same = _heldSrcMats.Count == _heldCopyMats.Count;
                for (int i = 0; same && i < _heldSrcMats.Count; i++) same = ReferenceEquals(_heldSrcMats[i], _heldCopyMats[i]);
                if (!same) copy.sharedMaterials = _heldSrcMats.ToArray();
                if (src.HasPropertyBlock()) { src.GetPropertyBlock(_heldBlock); copy.SetPropertyBlock(_heldBlock); }
                else if (copy.HasPropertyBlock()) copy.SetPropertyBlock(null);
            }
            if (_heldLogs2 < 3 && Time.frameCount % 120 == 0)
            {
                _heldLogs2++;
                var cam = Camera.main;
                _log.LogInfo($"V1 held item: '{m.Item.name}' copy at {rt.position:F2} (hand {_playerLeftHand.position:F2}{(cam != null ? $", camera {cam.transform.position:F2}" : "")}), " +
                             $"scale {rt.lossyScale:F3}, viewmodel item at {it.position:F2} scale {it.lossyScale:F3}");
            }
        }
    }
}
