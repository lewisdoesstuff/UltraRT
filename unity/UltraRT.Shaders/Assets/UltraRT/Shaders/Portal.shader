// Hit shader of the portal quads (src/UltraRT/SceneProxies.Portals.cs, docs/portals.md). One material per portal side;
// _PortalIndex selects its travel matrix in _UltraRTPortals. The ray generation shader does the teleport
// (UltraRTPortals.hlsl, TraceScene); this only reports where the ray met the portal.
Shader "UltraRT/Portal"
{
    Properties
    {
        _PortalIndex ("Portal side index", Float) = 0
        _PortalNoRecursion ("Hidden in portal views (appearsInRecursions off)", Float) = 0
        _PortalSeesLayer ("Portal view sees layer 30 (canSeePortalLayer)", Float) = 1
        _PortalMirror ("Portal mirror (exit = entry)", Float) = 0
        _PortalNoSelf ("Hidden in its own view (canSeeItself off)", Float) = 0
        _PortalInfinite ("supportInfiniteRecursion", Float) = 0
        _PortalMaxRec ("maxRecursions (the game's, before the raster trim)", Float) = 3
        _PortalSky ("Sky slot behind this side (0 = the level's sky)", Float) = 0
    }

    SubShader
    {
        // Raster pass that draws nothing, in case a portal quad ever reaches a camera.
        Pass
        {
            Name "NoDraw"
            ColorMask 0
            ZWrite Off
            ZTest Never
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert() : SV_POSITION { return float4(0, 0, -2, 1); }
            float4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "UltraRTHit"
            Tags { "LightMode" = "RayTracing" }

            HLSLPROGRAM
            #pragma raytracing UltraRTHit

            #include "UltraRTCommon.hlsl"
            #define ULTRART_PORTALS_DEFINES_ONLY
            #include "UltraRTPortals.hlsl"

            float _PortalIndex;
            float _PortalNoRecursion;
            float _PortalSeesLayer;
            float _PortalMirror;
            float _PortalNoSelf;
            float _PortalInfinite;
            float _PortalMaxRec;
            float _PortalSky;

            // Only rays crossing the side from its back half-space along its forward axis enter it (PortalMath.Raycast);
            // from behind, the quad is not there.
            [shader("anyhit")]
            void AnyHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                float3x4 o2w = ObjectToWorld3x4();
                float3 fwd = float3(o2w[0][2], o2w[1][2], o2w[2][2]);
                if (dot(WorldRayDirection(), fwd) <= 0.0) IgnoreHit();
                // Seen from inside another portal's view: the game doesn't draw this side there.
                if (_PortalNoRecursion > 0.5 && (p.flags & ULTRART_PORTAL_IN_RECURSION) != 0u) IgnoreHit();
            }

            [shader("closesthit")]
            void ClosestHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                p.t = RayTCurrent();
                p.instance = (uint)_PortalIndex;
                p.flags = ULTRART_FLAG_PORTAL;
                p.normal = -WorldRayDirection();
                p.albedo = 0;
                p.alpha = 0;
                // Bit 0: ray gen adds layer-30 geometry to the next segment's mask; bit 1: the side is a mirror; bit 2:
                // canSeeItself off; bit 3: supportInfiniteRecursion; bits 8-15: maxRecursions; bits 16-23: the sky slot
                // behind the side (overrideSkyboxEnter/Exit; UltraRTPortals.hlsl, ULTRART_PORTAL_*).
                p.material = (_PortalSeesLayer > 0.5 ? 1u : 0u) | (_PortalMirror > 0.5 ? 2u : 0u) | (_PortalNoSelf > 0.5 ? 4u : 0u)
                           | (_PortalInfinite > 0.5 ? 8u : 0u) | ((uint)clamp(_PortalMaxRec, 0.0, 255.0) << 8)
                           | ((uint)clamp(_PortalSky, 0.0, 255.0) << 16);
                p.motion = 0;
            }
            ENDHLSL
        }
    }
}
