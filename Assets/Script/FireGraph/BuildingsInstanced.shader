// Instanced building boxes coloured by fire state at _SimTime (FireGraphViewer).
// One instance per building; data comes from structured buffers indexed by SV_InstanceID.
Shader "FireGraph/BuildingsInstanced"
{
    Properties
    {
        _Unburned   ("Unburned",   Color) = (0.62, 0.63, 0.66, 1)
        _Incubating ("Incubating", Color) = (0.55, 0.20, 0.08, 1)
        _Burning    ("Burning",    Color) = (1.00, 0.55, 0.05, 1)
        _BurntOut   ("Burnt out",  Color) = (0.08, 0.07, 0.07, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            StructuredBuffer<float4> _PosSize;   // x_m, y_m, side_m, height_m
            StructuredBuffer<float4> _Profile;   // tau0, tg, td, tx  [s]
            StructuredBuffer<float>  _TIgnBuf;   // ignition time [s], inf = never
            float  _SimTime;
            float4 _Unburned, _Incubating, _Burning, _BurntOut;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float4 col : COLOR0; };

            float4 StateColor(uint id)
            {
                float t = _SimTime - _TIgnBuf[id];
                if (!(t >= 0.0)) return _Unburned;                 // also catches inf
                float4 p = _Profile[id];
                if (t <= p.x) return _Incubating;
                float s = t - p.x;
                float phi;
                if (s < p.y) phi = s / p.y;
                else if (s < p.y + p.z) phi = 1.0;
                else if (s < p.y + p.z + p.w) phi = 1.0 - (s - p.y - p.z) / p.w;
                else return _BurntOut;
                return lerp(_Incubating, _Burning, saturate(phi));
            }

            v2f vert(appdata v, uint id : SV_InstanceID)
            {
                float4 ps = _PosSize[id];
                float3 w = float3(ps.x, 0.0, ps.y) + v.vertex.xyz * float3(ps.z, ps.w, ps.z) + float3(0.0, 0.5 * ps.w, 0.0);
                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(w, 1.0));
                float shade = 0.75 + 0.25 * saturate(dot(v.normal, normalize(float3(0.4, 1.0, 0.3))));
                o.col = StateColor(id) * shade;
                return o;
            }

            float4 frag(v2f i) : SV_Target { return i.col; }
            ENDHLSL
        }
    }
}
