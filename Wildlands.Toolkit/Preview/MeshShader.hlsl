cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 ShadowProjection;
    float4 CameraPosition;
    float4 KeyDirection;
    float4 KeyColour;
    float4 FillDirection;
    float4 FillColour;
    float4 RimDirection;
    float4 RimColour;
    float4 SkyColour;
    float4 GroundColour;
    float4 BackgroundTop;
    float4 BackgroundBottom;
    float4 Viewport;
    float4 Ground;
};

cbuffer Material : register(b1)
{
    float4 Mode;
    float4 BaseColour;
    float4 Options;
    float4 BaseHsv;
    float4 MaskR;
    float4 MaskG;
    float4 MaskB;
    float4 FinalHsv;
    float4 Camo;
    float4 CamoZones;
    float4 CamoUv0;
    float4 CamoUv1;
    float4 DetailUv0;
    float4 DetailUv1;
    float4 DetailNormalUv0;
    float4 DetailNormalUv1;
    float4 UvAb;
    float4 Reflect;
    float4 ClothReflect;
    float4 Detail;
    float4 DetailWhite;
    float4 DetailBlack;
    float4 DetailNormal;
    float4 DirtColour;
    float4 Dirt;
    float4 Blend;
};

Texture2D Diffuse : register(t0);
Texture2D Normal : register(t1);
Texture2D Mask : register(t2);
Texture2D CamoPattern : register(t3);
Texture2D DetailDiffuse : register(t4);
Texture2D DetailCotton : register(t5);
Texture2D DetailLeather : register(t6);
Texture2D DetailMetal : register(t7);
Texture2D ShadowMap : register(t8);
Texture2D Specular : register(t9);

SamplerState Wrap : register(s0);
SamplerComparisonState ShadowSampler : register(s1);

static const float Pi = 3.14159265;

struct Vertex
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float3 Tangent : TANGENT;
    float3 Binormal : BINORMAL;
    float2 UvA : TEXCOORD0;
    float2 UvB : TEXCOORD1;
    float4 Paint : COLOR;
};

struct Pixel
{
    float4 Position : SV_Position;
    float3 World : WORLD;
    float3 Normal : NORMAL;
    float3 Tangent : TANGENT;
    float3 Binormal : BINORMAL;
    float4 Uv : TEXCOORD0;
    float4 Paint : COLOR;
};

struct Surface
{
    float3 Albedo;
    float3 Reflectance;
    float3 Normal;
    float Roughness;
    float Alpha;
};

Pixel MeshVS(Vertex v)
{
    Pixel p;
    p.Position = mul(float4(v.Position, 1), ViewProjection);
    p.World = v.Position;
    p.Normal = v.Normal;
    p.Tangent = v.Tangent;
    p.Binormal = v.Binormal;
    p.Uv = float4(v.UvA, v.UvB);
    p.Paint = v.Paint;
    return p;
}

float4 ShadowVS(Vertex v) : SV_Position
{
    return mul(float4(v.Position, 1), ShadowProjection);
}

float4 BackgroundVS(uint id : SV_VertexID) : SV_Position
{
    float2 corner = float2((id << 1) & 2, id & 2);
    return float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
}

float3 BackgroundAt(float4 position)
{
    return lerp(BackgroundTop.rgb, BackgroundBottom.rgb, saturate(position.y / Viewport.y));
}

float4 BackgroundPS(float4 position : SV_Position) : SV_Target
{
    return float4(BackgroundAt(position), 1);
}

float2 Transform(float2 uv, float4 row0, float4 row1)
{
    return float2(dot(float3(uv, 1), row0.xyz), dot(float3(uv, 1), row1.xyz));
}

float3 TransformHsv(float3 colour, float3 hsv)
{
    float value = hsv.z;
    float angle = hsv.x * 6.28;
    float u = value * hsv.y * cos(angle);
    float w = value * hsv.y * sin(angle);

    return float3(
        (0.299 * value + 0.701 * u + 0.168 * w) * colour.r
            + (0.587 * value - 0.587 * u + 0.330 * w) * colour.g
            + (0.114 * value - 0.114 * u - 0.497 * w) * colour.b,
        (0.299 * value - 0.299 * u - 0.328 * w) * colour.r
            + (0.587 * value + 0.413 * u + 0.035 * w) * colour.g
            + (0.114 * value - 0.114 * u + 0.292 * w) * colour.b,
        (0.299 * value - 0.300 * u + 1.250 * w) * colour.r
            + (0.587 * value - 0.588 * u - 1.050 * w) * colour.g
            + (0.114 * value + 0.886 * u - 0.203 * w) * colour.b);
}

float Overlay(float shade, float camo)
{
    return shade > 0.5 ? 1 - (1 - 2 * (shade - 0.5)) * (1 - camo) : 2 * shade * camo;
}

float3 Unpack(float2 xy)
{
    float2 n = xy * 2 - 1;
    return float3(n, sqrt(saturate(1 - dot(n, n))));
}

float3 Reoriented(float3 base, float3 detail)
{
    float3 t = base + float3(0, 0, 1);
    float3 u = detail * float3(-1, -1, 1);
    return normalize(t * dot(t, u) / t.z - u);
}

float3 Reflectance(float3 albedo, float cotton, float velvet, float leather, float metal)
{
    float highest = max(albedo.r, max(albedo.g, albedo.b));
    float lowest = min(albedo.r, min(albedo.g, albedo.b));
    float range = highest - lowest;

    float hue = 0;
    float saturation = 0;
    if (range > 0)
    {
        float red = albedo.r >= highest ? (albedo.g - albedo.b) / range + 6 : 0;
        float green = albedo.g >= highest ? (albedo.b - albedo.r) / range + 2 : 0;
        float blue = albedo.b >= highest ? (albedo.r - albedo.g) / range + 4 : 0;
        hue = frac(max(red, max(green, blue)) / 6);
        saturation = range / highest;
    }

    hue *= cotton * ClothReflect.x + velvet * ClothReflect.x + metal * Reflect.y;
    saturation *= metal * Reflect.z + velvet * ClothReflect.y;
    float intensity = cotton * 0.004 + velvet * 0.004 + leather * 0.04 + metal * Reflect.x;

    float3 colour = saturate(float3(abs(hue * 6 - 3) - 1, 2 - abs(hue * 6 - 2), 2 - abs(hue * 6 - 4)));
    return lerp(1, colour, saturation) * intensity;
}

float DirtAmount(float4 m, float height)
{
    float wound = DirtColour.w;
    float initial = max(wound, Dirt.x);
    float blend = max(Dirt.y, wound);
    float overlay = max(wound, Dirt.z) + 0.5;

    float low = saturate(1.5 - overlay);
    float high = saturate(2.5 - overlay);
    float up = saturate((1 - height * 0.1332 - low) / max(high - low, 0.001));

    float from = min(1.125 - up, 1);
    float to = min(1.375 - up, 1);
    float cavity = saturate((m.r - from) / max(to - from, 0.001));

    return saturate(cavity * 0.9 * (blend - initial * m.g) + initial * m.g);
}

Surface ClothSurface(Pixel p)
{
    bool simplified = Mode.x > 1.5;
    float2 uvA = p.Uv.xy;
    float2 uvB = p.Uv.zw;
    float2 uv = lerp(uvA, uvB, UvAb.x);

    float4 d = Diffuse.Sample(Wrap, uv);
    float4 m = Mode.z > 0.5 ? Mask.Sample(Wrap, uv) : float4(0, 0, 0, 192.0 / 255.0);

    float velvet = simplified ? 1 : saturate((m.a - 0.752941) * 4.047619);
    float clothes = simplified ? 1 : saturate((m.a - 0.501961) * 4.015748);
    float cotton = clothes - velvet;
    float metal = simplified ? 0 : saturate((0.247059 - m.a) * 4.047619);
    float leather = 1 - (clothes + metal);

    float3 tint = pow(saturate(p.Paint.rgb), 2.2);
    float white = tint.r * tint.g * tint.b;
    tint -= white;

    float3 hsv = lerp(1, BaseHsv.xyz, white);
    hsv = lerp(hsv, MaskR.xyz, tint.r);
    hsv = lerp(hsv, MaskG.xyz, tint.g);
    hsv = lerp(hsv, MaskB.xyz, tint.b);

    float3 albedo = lerp(TransformHsv(d.rgb, hsv), d.rgb, Camo.w);

    float shade = d.g + Camo.x;
    float3 pattern = CamoPattern.Sample(Wrap, Transform(lerp(uvA, uvB, Camo.z), CamoUv0, CamoUv1)).rgb;
    float3 overlaid = float3(Overlay(shade, pattern.r), Overlay(shade, pattern.g), Overlay(shade, pattern.b));
    float camo = p.Paint.a * Camo.y * dot(CamoZones, float4(cotton, velvet, leather, metal));
    albedo = lerp(albedo, overlaid, camo);

    float3 reflectance = Reflectance(albedo, cotton, velvet, leather, metal);

    albedo *= clothes + leather + metal * 0.02;

    float fabric = DetailDiffuse.Sample(Wrap, Transform(lerp(uvA, uvB, UvAb.z), DetailUv0, DetailUv1)).b;
    float3 detail = lerp(DetailBlack.rgb, DetailWhite.rgb, fabric);
    float detailAmount = simplified ? Detail.x * 0.6 : cotton * Detail.x + velvet * Detail.y;
    albedo *= lerp(1, detail, detailAmount);

    albedo = lerp(albedo, DirtColour.rgb, DirtAmount(m, p.World.z));

    albedo = TransformHsv(albedo, FinalHsv.xyz);
    reflectance = TransformHsv(reflectance, FinalHsv.xyz);

    float4 n = Mode.y > 0.5 ? Normal.Sample(Wrap, lerp(uvA, uvB, UvAb.y)) : float4(0.5, 0.5, 1, 0.3);
    float3 normal = Unpack(n.xy);

    float2 fabricUv = Transform(lerp(uvA, uvB, UvAb.z), DetailUv0, DetailUv1);
    float2 detailUv = Transform(uvA, DetailNormalUv0, DetailNormalUv1);
    float4 flat = float4(0, 0, 1, 1);
    float4 cottonDetail = DetailCotton.Sample(Wrap, fabricUv);
    float4 leatherDetail = DetailLeather.Sample(Wrap, detailUv);
    float4 metalDetail = DetailMetal.Sample(Wrap, detailUv);
    float cottonWeight = cotton * DetailNormal.x + velvet * DetailNormal.w;
    float leatherWeight = simplified ? 0 : leather * DetailNormal.y;
    float metalWeight = simplified ? 0 : metal * DetailNormal.z;
    float4 detailNormal = flat
        + cottonWeight * (float4(Unpack(cottonDetail.xy), cottonDetail.a) - flat)
        + leatherWeight * (float4(Unpack(leatherDetail.xy), leatherDetail.a) - flat)
        + metalWeight * (float4(Unpack(metalDetail.xy), metalDetail.a) - flat);
    normal = Reoriented(normal, normalize(detailNormal.xyz));

    float gloss = log(1 / (exp(-19 * n.a) + exp(-19 * saturate(detailNormal.w)))) / 19;
    gloss = max(saturate(gloss), (1 - Reflect.w) * metal);

    Surface s;
    s.Albedo = max(albedo, 0);
    s.Reflectance = max(reflectance, 0);
    s.Normal = normal;
    s.Roughness = clamp(1 - gloss, 0.08, 1);
    s.Alpha = d.a;
    return s;
}

Surface PlainSurface(Pixel p)
{
    float4 d = Mode.w > 0.5 ? Diffuse.Sample(Wrap, p.Uv.xy) : float4(1, 1, 1, 1);
    float4 n = Mode.y > 0.5 ? Normal.Sample(Wrap, p.Uv.xy) : float4(0.5, 0.5, 1, 1);

    float gloss = Options.z > 0.5 ? n.a : 1 - Options.y;

    Surface s;
    s.Albedo = d.rgb * BaseColour.rgb;
    s.Reflectance = Options.w > 0.5 ? Specular.Sample(Wrap, p.Uv.xy).rgb : 0.04;
    s.Normal = Unpack(n.xy);
    s.Roughness = clamp(1 - gloss, 0.08, 1);
    s.Alpha = d.a;
    return s;
}

float3 Direct(Surface s, float3 n, float3 v, float3 l, float3 colour)
{
    float nl = saturate(dot(n, l));
    if (nl <= 0)
        return 0;

    float3 h = normalize(l + v);
    float nv = max(dot(n, v), 0.0001);
    float nh = saturate(dot(n, h));
    float vh = saturate(dot(v, h));

    float a = s.Roughness * s.Roughness;
    float a2 = a * a;
    float spread = nh * nh * (a2 - 1) + 1;
    float distribution = a2 / (Pi * spread * spread);
    float k = (s.Roughness + 1) * (s.Roughness + 1) / 8;
    float visibility = 1 / ((nl * (1 - k) + k) * (nv * (1 - k) + k));
    float3 fresnel = s.Reflectance + (1 - s.Reflectance) * pow(1 - vh, 5);

    float3 specular = distribution * visibility * fresnel / 4;
    float3 diffuse = s.Albedo / Pi * (1 - fresnel);
    return (diffuse + specular) * colour * nl;
}

float3 EnvironmentBrdf(float3 f0, float roughness, float nv)
{
    float4 c0 = float4(-1, -0.0275, -0.572, 0.022);
    float4 c1 = float4(1, 0.0425, 1.04, -0.04);
    float4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * nv)) * r.x + r.y;
    float2 ab = float2(-1.04, 1.04) * a004 + r.zw;
    return f0 * ab.x + ab.y;
}

float3 Sky(float3 direction)
{
    return lerp(GroundColour.rgb, SkyColour.rgb, saturate(direction.z * 0.5 + 0.5));
}

float3 Studio(float3 direction, float roughness)
{
    float top = smoothstep(-0.1, 0.75, direction.z);
    float3 sharp = lerp(GroundColour.rgb * 0.5, SkyColour.rgb * 1.8, top * top);
    return lerp(sharp, Sky(direction), roughness * roughness);
}

float ShadowAt(float3 world)
{
    if (Viewport.w < 0.5)
        return 1;

    float4 shadow = mul(float4(world, 1), ShadowProjection);
    float2 uv = shadow.xy * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0) || any(uv > 1))
        return 1;

    float depth = shadow.z - 0.0015;
    float texel = 1.0 / 2048.0;
    float lit = 0;
    [unroll] for (int y = -2; y <= 2; y++)
        [unroll] for (int x = -2; x <= 2; x++)
            lit += ShadowMap.SampleCmpLevelZero(ShadowSampler, uv + float2(x, y) * texel * 1.5, depth);
    return lit / 25;
}

float3 Tonemap(float3 colour)
{
    colour *= Viewport.z;
    return saturate((colour * (2.51 * colour + 0.03)) / (colour * (2.43 * colour + 0.59) + 0.14));
}

float3 Light(Surface s, Pixel p, bool front)
{
    float3 geometric = normalize(front ? p.Normal : -p.Normal);
    float3 tangent = normalize(p.Tangent - geometric * dot(geometric, p.Tangent));
    float3 binormal = normalize(front ? p.Binormal : -p.Binormal);
    float3 n = normalize(tangent * s.Normal.x + binormal * s.Normal.y + geometric * s.Normal.z);
    if (any(isnan(n)))
        n = geometric;

    float3 v = normalize(CameraPosition.xyz - p.World);
    float nv = saturate(dot(n, v));

    float3 colour = Direct(s, n, v, -KeyDirection.xyz, KeyColour.rgb) * ShadowAt(p.World)
        + Direct(s, n, v, -FillDirection.xyz, FillColour.rgb)
        + Direct(s, n, v, -RimDirection.xyz, RimColour.rgb);

    colour += s.Albedo * Sky(n);
    colour += Studio(reflect(-v, n), s.Roughness) * EnvironmentBrdf(s.Reflectance, s.Roughness, nv);
    return colour;
}

float4 MeshPS(Pixel p, bool front : SV_IsFrontFace) : SV_Target
{
    Surface s;
    if (Mode.x > 0.5)
        s = ClothSurface(p);
    else
        s = PlainSurface(p);

    if (Options.x > 0.5 && s.Alpha < 0.5 || Blend.x > 0.5 && s.Alpha < 0.02)
        discard;

    return float4(Tonemap(Light(s, p, front)), Blend.x > 0.5 ? s.Alpha : 1);
}

float4 UnlitPS(Pixel p) : SV_Target
{
    return float4(BaseColour.rgb, 1);
}

float4 GroundPS(Pixel p) : SV_Target
{
    float distance = length(p.World.xy - Ground.xy) / max(Ground.z, 0.0001);
    float fade = saturate(1 - distance);
    float shadow = ShadowAt(p.World);
    return float4(BackgroundAt(p.Position) * lerp(1, 0.35, (1 - shadow) * fade * fade), 1);
}
