using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// The player's own lights through portals (docs/portals.md, "The player's light through portals"; off by default).
    /// The game's player light sits at the eye and a muzzle flash at the gun. What the camera sees through a portal is
    /// elsewhere in the world, so neither lights it, in the game (a Unity light lights its own place; the portal camera
    /// renders the far side with the far side's lights) as here.
    ///
    /// With bench plugin._portalLights=1, every packed point light the player owns (<see cref="PlayerOwnedLight"/>) gets a
    /// virtual copy behind each portal side it can shine through: at its image (the side's travel matrix), as a spot whose
    /// cone just holds the exit's opening, unshadowed (l3.z 0.75) like the eye light itself. Packed with the surface
    /// lights, before the lights that light the air only. Mirrors are skipped. Off by default: it departs from the game,
    /// and in 8-2 (the player light: intensity 0.5, range 15) it made no visible difference.
    /// </summary>
    public partial class Plugin
    {
        private bool _portalLights = false;
        private const int MaxPortalLightImages = 8;
        private readonly List<SceneProxies.PortalImage> _portalImages = new List<SceneProxies.PortalImage>();
        private int _portalLightLogs, _portalLightLogged = -1;

        /// <summary>GatherLights, after the effect lights and before the air-only lights: the images of the player's lights.</summary>
        private void AppendPortalLightImages()
        {
            int added = 0;
            if (_portalLights && _portals && _proxies != null && _camera != null && _proxies.PortalSideCount > 0)
            {
                PlayerLightTestBegin();
                int end = _lightCount;
                for (int i = 0; i < end && _lightCount < MaxLights && added < MaxPortalLightImages; i++)
                {
                    var l0 = _lightData[i * 4];
                    var l3 = _lightData[i * 4 + 3];
                    if (l0.w == 0f || l3.x < -1.5f || l3.x > -0.5f) continue; // point lights only: no sun, spot or area light
                    if (!PlayerOwnedLight(i)) continue;
                    _proxies.PortalImages(new Vector3(l0.x, l0.y, l0.z), 2f * l0.w, _portalImages); // the falloff ends at 2 x range
                    foreach (var im in _portalImages)
                    {
                        if (_lightCount >= MaxLights || added >= MaxPortalLightImages) break;
                        int j = _lightCount * 4;
                        _lightData[j] = new Vector4(im.Pos.x, im.Pos.y, im.Pos.z, l0.w);
                        _lightData[j + 1] = _lightData[i * 4 + 1];
                        _lightData[j + 2] = new Vector4(im.Axis.x, im.Axis.y, im.Axis.z, 0f);
                        _lightData[j + 3] = new Vector4(im.CosHalf, 1f / Mathf.Max(im.CosInner - im.CosHalf, 1e-4f), 0.75f, l3.w);
                        _lightMasks[_lightCount] = _lightMasks[i];
                        _packedLights[_lightCount] = null;
                        _lightIds[_lightCount] = unchecked((int)(0x20000000u | ((uint)(SourceId(i) * 31 + im.Side) & 0x1FFFFFFFu)));
                        _lightPlaced[_lightCount] = false;
                        _lightMedium[_lightCount] = 0f; // the air behind a portal: the volumetric pass stops at the plane anyway
                        _lightCount++;
                        added++;
                    }
                }
            }
            if (added != _portalLightLogged && _portalLightLogs < 40)
            {
                _portalLightLogs++;
                _portalLightLogged = added;
                Logger.LogInfo($"Portal light images: {added} (player lights imaged through portals)");
            }
        }

        private int SourceId(int i)
        {
            var l = _packedLights[i];
            return !ReferenceEquals(l, null) && l != null ? l.GetInstanceID() : _lightIds[i];
        }
    }
}
