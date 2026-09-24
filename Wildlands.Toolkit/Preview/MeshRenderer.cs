using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Textures;
using static Vortice.Direct3D11.D3D11;
using Format = Vortice.DXGI.Format;

namespace Wildlands.Toolkit;

[StructLayout(LayoutKind.Sequential)]
public struct RenderVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Tangent;
    public Vector3 Binormal;
    public Vector2 UvA;
    public Vector2 UvB;
    public Vector4 Paint;
}

public sealed class RenderMaterial
{
    public TexturePixels? Diffuse { get; init; }
    public TexturePixels? Normal { get; init; }
    public TexturePixels? Mask { get; init; }
    public TexturePixels? Camo { get; init; }
    public TexturePixels? DetailDiffuse { get; init; }
    public TexturePixels? DetailCotton { get; init; }
    public TexturePixels? DetailLeather { get; init; }
    public TexturePixels? DetailMetal { get; init; }
    public TexturePixels? Specular { get; init; }
    public ClothParameters? Cloth { get; init; }
    public bool AlphaTest { get; init; }
    public bool GlossInNormal { get; init; }
}

public sealed class RenderPart
{
    public required int IndexStart { get; init; }
    public required int IndexCount { get; init; }
    public required int VertexCount { get; init; }
    public required Rect3D Bounds { get; init; }
    public int TriangleCount => IndexCount / 3;
}

public sealed class RenderDraw
{
    public required RenderPart Part { get; init; }
    public RenderMaterial? Material { get; init; }
    public required System.Windows.Media.Color Colour { get; init; }
    public bool TwoSided { get; init; }
    public bool Translucent { get; init; }
}

public sealed class RenderOverlay
{
    public required Vector3[] Positions { get; init; }
    public required int[] Indices { get; init; }
    public required System.Windows.Media.Color Colour { get; init; }
}

public sealed class MeshRenderer : IDisposable
{
    const int Samples = 4;
    const int ShadowSize = 2048;

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _context;

    readonly ID3D11VertexShader _meshVertex;
    readonly ID3D11VertexShader _shadowVertex;
    readonly ID3D11VertexShader _backgroundVertex;
    readonly ID3D11PixelShader _meshPixel;
    readonly ID3D11PixelShader _unlitPixel;
    readonly ID3D11PixelShader _groundPixel;
    readonly ID3D11PixelShader _backgroundPixel;
    readonly ID3D11InputLayout _layout;

    readonly ID3D11RasterizerState _cullBack;
    readonly ID3D11RasterizerState _cullNone;
    readonly ID3D11RasterizerState _shadowRaster;
    readonly ID3D11DepthStencilState _depthOn;
    readonly ID3D11DepthStencilState _depthRead;
    readonly ID3D11DepthStencilState _depthOff;
    readonly ID3D11BlendState _opaque;
    readonly ID3D11BlendState _blend;
    readonly ID3D11SamplerState _wrap;
    readonly ID3D11SamplerState _shadowSampler;

    readonly ID3D11Buffer _frameBuffer;
    readonly ID3D11Buffer _materialBuffer;

    readonly ID3D11Texture2D _shadowTexture;
    readonly ID3D11DepthStencilView _shadowDepth;
    readonly ID3D11ShaderResourceView _shadowView;

    readonly ID3D11ShaderResourceView _white;
    readonly ID3D11ShaderResourceView _black;
    readonly ID3D11ShaderResourceView _flat;

    readonly Dictionary<TexturePixels, ID3D11ShaderResourceView> _textures = new(ReferenceEqualityComparer.Instance);

    ID3D11Buffer? _vertices;
    ID3D11Buffer? _indices;
    ID3D11Buffer? _overlayVertices;
    ID3D11Buffer? _overlayIndices;
    readonly List<(int Start, int Count, System.Windows.Media.Color Colour)> _overlays = [];
    ID3D11Buffer? _groundVertices;

    int _width;
    int _height;
    ID3D11Texture2D? _colour;
    ID3D11RenderTargetView? _colourView;
    ID3D11Texture2D? _depth;
    ID3D11DepthStencilView? _depthView;
    ID3D11Texture2D? _resolved;
    ID3D11Texture2D? _staging;

    public MeshRenderer()
    {
        var levels = new[] { FeatureLevel.Level_11_0 };
        if (D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? device, out ID3D11DeviceContext? context).Failure)
            D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out context).CheckError();
        _device = device!;
        _context = context!;

        string source = ShaderSource();
        _meshVertex = _device.CreateVertexShader(Compile(source, "MeshVS", "vs_5_0", out var meshCode));
        _shadowVertex = _device.CreateVertexShader(Compile(source, "ShadowVS", "vs_5_0", out _));
        _backgroundVertex = _device.CreateVertexShader(Compile(source, "BackgroundVS", "vs_5_0", out _));
        _meshPixel = _device.CreatePixelShader(Compile(source, "MeshPS", "ps_5_0", out _));
        _unlitPixel = _device.CreatePixelShader(Compile(source, "UnlitPS", "ps_5_0", out _));
        _groundPixel = _device.CreatePixelShader(Compile(source, "GroundPS", "ps_5_0", out _));
        _backgroundPixel = _device.CreatePixelShader(Compile(source, "BackgroundPS", "ps_5_0", out _));

        _layout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32_Float, 24, 0),
            new InputElementDescription("BINORMAL", 0, Format.R32G32B32_Float, 36, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 48, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32_Float, 56, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 64, 0),
        ], meshCode);

        _cullBack = _device.CreateRasterizerState(new RasterizerDescription(CullMode.Back, FillMode.Solid) { FrontCounterClockwise = true, MultisampleEnable = true, DepthClipEnable = true });
        _cullNone = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { FrontCounterClockwise = true, MultisampleEnable = true, DepthClipEnable = true });
        _shadowRaster = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthBias = 200, SlopeScaledDepthBias = 2f, DepthClipEnable = true });
        _depthOn = _device.CreateDepthStencilState(DepthStencilDescription.Default);
        _depthRead = _device.CreateDepthStencilState(DepthStencilDescription.DepthRead);
        _depthOff = _device.CreateDepthStencilState(DepthStencilDescription.None);
        _opaque = _device.CreateBlendState(BlendDescription.Opaque);
        _blend = _device.CreateBlendState(BlendDescription.NonPremultiplied);
        _wrap = _device.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Wrap, TextureAddressMode.Wrap, TextureAddressMode.Wrap) { MaxAnisotropy = 16, MaxLOD = float.MaxValue });
        _shadowSampler = _device.CreateSamplerState(new SamplerDescription(Filter.ComparisonMinMagLinearMipPoint, TextureAddressMode.Border, TextureAddressMode.Border, TextureAddressMode.Border)
        {
            ComparisonFunc = ComparisonFunction.LessEqual,
            BorderColor = new Color4(1, 1, 1, 1),
            MaxLOD = float.MaxValue,
        });

        _frameBuffer = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer));
        _materialBuffer = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<MaterialConstants>(), BindFlags.ConstantBuffer));

        _shadowTexture = _device.CreateTexture2D(new Texture2DDescription(Format.R32_Typeless, ShadowSize, ShadowSize, 1, 1, BindFlags.DepthStencil | BindFlags.ShaderResource));
        _shadowDepth = _device.CreateDepthStencilView(_shadowTexture, new DepthStencilViewDescription(DepthStencilViewDimension.Texture2D, Format.D32_Float));
        _shadowView = _device.CreateShaderResourceView(_shadowTexture, new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D, Format.R32_Float, 0, 1));

        _white = Solid(255, 255, 255, 255);
        _black = Solid(0, 0, 0, 255);
        _flat = Solid(255, 128, 128, 255);
    }

    public void SetGeometry(RenderVertex[] vertices, uint[] indices)
    {
        _vertices?.Dispose();
        _indices?.Dispose();
        _vertices = vertices.Length == 0 ? null : _device.CreateBuffer(vertices, BindFlags.VertexBuffer);
        _indices = indices.Length == 0 ? null : _device.CreateBuffer(indices, BindFlags.IndexBuffer);
    }

    public void SetOverlays(IReadOnlyList<RenderOverlay> overlays)
    {
        _overlayVertices?.Dispose();
        _overlayIndices?.Dispose();
        _overlayVertices = null;
        _overlayIndices = null;
        _overlays.Clear();

        var vertices = new List<RenderVertex>();
        var indices = new List<uint>();

        foreach (var overlay in overlays)
        {
            int first = vertices.Count;
            _overlays.Add((indices.Count, overlay.Indices.Length, overlay.Colour));

            foreach (var position in overlay.Positions)
                vertices.Add(new RenderVertex { Position = position, Normal = Vector3.UnitZ, Paint = Vector4.One });
            foreach (int index in overlay.Indices)
                indices.Add((uint)(first + index));
        }

        if (indices.Count == 0)
            return;

        _overlayVertices = _device.CreateBuffer(vertices.ToArray(), BindFlags.VertexBuffer);
        _overlayIndices = _device.CreateBuffer(indices.ToArray(), BindFlags.IndexBuffer);
    }

    public void Render(WriteableBitmap target, PerspectiveCamera camera, IReadOnlyList<RenderDraw> draws, bool textured, bool? twoSided, Rect3D bounds, bool ground)
    {
        int width = target.PixelWidth;
        int height = target.PixelHeight;
        Resize(width, height);

        var centre = new Vector3((float)(bounds.X + bounds.SizeX / 2), (float)(bounds.Y + bounds.SizeY / 2), (float)(bounds.Z + bounds.SizeZ / 2));
        float radius = MathF.Max((float)(new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length / 2), 0.01f);
        if (bounds.IsEmpty)
        {
            centre = Vector3.Zero;
            radius = 1;
        }

        var frame = Frame(camera, width, height, centre, radius, bounds, ground);
        _context.UpdateSubresource(frame, _frameBuffer);

        _context.IASetInputLayout(_layout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetConstantBuffer(0, _frameBuffer);
        _context.PSSetConstantBuffer(0, _frameBuffer);
        _context.VSSetConstantBuffer(1, _materialBuffer);
        _context.PSSetConstantBuffer(1, _materialBuffer);
        _context.PSSetSampler(0, _wrap);
        _context.PSSetSampler(1, _shadowSampler);

        DrawShadows(draws);

        _context.OMSetRenderTargets(_colourView!, _depthView);
        _context.RSSetViewport(0, 0, width, height);
        _context.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth, 1, 0);
        _context.PSSetShaderResource(8, _shadowView);

        _context.OMSetBlendState(_opaque);
        _context.OMSetDepthStencilState(_depthOff);
        _context.RSSetState(_cullNone);
        _context.VSSetShader(_backgroundVertex);
        _context.PSSetShader(_backgroundPixel);
        _context.IASetInputLayout(null);
        _context.Draw(3, 0);
        _context.IASetInputLayout(_layout);

        if (ground && !bounds.IsEmpty)
            DrawGround(centre, radius, (float)bounds.Z);

        if (_vertices is not null && _indices is not null)
        {
            _context.IASetVertexBuffer(0, _vertices, (uint)Marshal.SizeOf<RenderVertex>());
            _context.IASetIndexBuffer(_indices, Format.R32_UInt, 0);
            _context.VSSetShader(_meshVertex);
            _context.PSSetShader(_meshPixel);

            _context.OMSetDepthStencilState(_depthOn);
            foreach (var draw in draws.Where(draw => !draw.Translucent || !textured))
                DrawPart(draw, textured, twoSided);

            _context.OMSetBlendState(_blend);
            _context.OMSetDepthStencilState(_depthRead);
            foreach (var draw in draws.Where(draw => draw.Translucent && textured))
                DrawPart(draw, textured, twoSided);
            _context.OMSetBlendState(_opaque);
        }

        if (_overlayVertices is not null && _overlayIndices is not null)
        {
            _context.OMSetDepthStencilState(_depthOn);
            _context.RSSetState(_cullNone);
            _context.IASetVertexBuffer(0, _overlayVertices, (uint)Marshal.SizeOf<RenderVertex>());
            _context.IASetIndexBuffer(_overlayIndices, Format.R32_UInt, 0);
            _context.VSSetShader(_meshVertex);
            _context.PSSetShader(_unlitPixel);

            foreach (var (start, count, colour) in _overlays)
            {
                var material = new MaterialConstants { BaseColour = Linear(colour) };
                _context.UpdateSubresource(material, _materialBuffer);
                _context.DrawIndexed((uint)count, (uint)start, 0);
            }
        }

        _context.PSSetShaderResource(8, null!);
        _context.ResolveSubresource(_resolved!, 0, _colour!, 0, Format.B8G8R8A8_UNorm_SRgb);
        _context.CopyResource(_staging!, _resolved!);

        var mapped = _context.Map(_staging!, 0, MapMode.Read);
        try
        {
            target.WritePixels(new Int32Rect(0, 0, width, height), mapped.DataPointer, (int)(mapped.RowPitch * height), (int)mapped.RowPitch);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    void DrawShadows(IReadOnlyList<RenderDraw> draws)
    {
        _context.ClearDepthStencilView(_shadowDepth, DepthStencilClearFlags.Depth, 1, 0);
        if (_vertices is null || _indices is null)
            return;

        _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), _shadowDepth);
        _context.RSSetViewport(0, 0, ShadowSize, ShadowSize);
        _context.RSSetState(_shadowRaster);
        _context.OMSetDepthStencilState(_depthOn);
        _context.IASetVertexBuffer(0, _vertices, (uint)Marshal.SizeOf<RenderVertex>());
        _context.IASetIndexBuffer(_indices, Format.R32_UInt, 0);
        _context.VSSetShader(_shadowVertex);
        _context.PSSetShader(null);

        foreach (var draw in draws.Where(draw => !draw.Translucent))
            _context.DrawIndexed((uint)draw.Part.IndexCount, (uint)draw.Part.IndexStart, 0);
    }

    void DrawGround(Vector3 centre, float radius, float floor)
    {
        float size = radius * 4;
        var corners = new[]
        {
            new Vector3(centre.X - size, centre.Y - size, floor),
            new Vector3(centre.X + size, centre.Y - size, floor),
            new Vector3(centre.X + size, centre.Y + size, floor),
            new Vector3(centre.X - size, centre.Y - size, floor),
            new Vector3(centre.X + size, centre.Y + size, floor),
            new Vector3(centre.X - size, centre.Y + size, floor),
        };

        _groundVertices?.Dispose();
        _groundVertices = _device.CreateBuffer(corners.Select(corner => new RenderVertex { Position = corner, Normal = Vector3.UnitZ, Paint = Vector4.One }).ToArray(), BindFlags.VertexBuffer);

        _context.OMSetDepthStencilState(_depthOff);
        _context.RSSetState(_cullBack);
        _context.IASetVertexBuffer(0, _groundVertices, (uint)Marshal.SizeOf<RenderVertex>());
        _context.VSSetShader(_meshVertex);
        _context.PSSetShader(_groundPixel);
        _context.Draw(6, 0);
    }

    void DrawPart(RenderDraw draw, bool textured, bool? twoSided)
    {
        var material = textured ? draw.Material : null;
        _context.RSSetState(twoSided ?? draw.TwoSided ? _cullNone : _cullBack);
        _context.UpdateSubresource(Constants(material, draw.Colour, textured && draw.Translucent), _materialBuffer);

        _context.PSSetShaderResource(0, View(material?.Diffuse, _white));
        _context.PSSetShaderResource(1, View(material?.Normal, _flat));
        _context.PSSetShaderResource(2, View(material?.Mask, _white));
        _context.PSSetShaderResource(3, View(material?.Camo, _black));
        _context.PSSetShaderResource(4, View(material?.DetailDiffuse, _white));
        _context.PSSetShaderResource(5, View(material?.DetailCotton, _flat));
        _context.PSSetShaderResource(6, View(material?.DetailLeather, _flat));
        _context.PSSetShaderResource(7, View(material?.DetailMetal, _flat));
        _context.PSSetShaderResource(9, View(material?.Specular, _black));

        _context.DrawIndexed((uint)draw.Part.IndexCount, (uint)draw.Part.IndexStart, 0);
    }

    FrameConstants Frame(PerspectiveCamera camera, int width, int height, Vector3 centre, float radius, Rect3D bounds, bool ground)
    {
        var position = ToVector(camera.Position);
        var forward = Vector3.Normalize(ToVector(camera.LookDirection));
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        if (float.IsNaN(right.X))
            right = Vector3.UnitX;
        var up = Vector3.Cross(right, forward);

        float aspect = width / (float)height;
        float horizontal = (float)(camera.FieldOfView * Math.PI / 180);
        float vertical = 2 * MathF.Atan(MathF.Tan(horizontal / 2) / aspect);
        float distance = Vector3.Distance(position, centre);
        float near = MathF.Max((float)camera.NearPlaneDistance, 0.0001f);
        float far = distance + radius * 8;

        var view = Matrix4x4.CreateLookAt(position, position + forward, ToVector(camera.UpDirection));
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(vertical, aspect, near, far);

        var toKey = Vector3.Normalize(-forward * 0.55f + up * 0.75f - right * 0.55f);
        var toFill = Vector3.Normalize(-forward * 0.7f + right * 0.85f - up * 0.1f);
        var toRim = Vector3.Normalize(forward * 0.85f + up * 0.55f + right * 0.35f);

        var helper = MathF.Abs(toKey.Z) > 0.95f ? Vector3.UnitY : Vector3.UnitZ;
        var shadowView = Matrix4x4.CreateLookAt(centre + toKey * radius * 3, centre, helper);
        var shadowProjection = Matrix4x4.CreateOrthographic(radius * 2.4f, radius * 2.4f, radius * 0.5f, radius * 6);

        return new FrameConstants
        {
            ViewProjection = view * projection,
            ShadowProjection = shadowView * shadowProjection,
            CameraPosition = new Vector4(position, 1),
            KeyDirection = new Vector4(-toKey, 0),
            KeyColour = new Vector4(1.0f, 0.95f, 0.88f, 1) * 3.2f,
            FillDirection = new Vector4(-toFill, 0),
            FillColour = new Vector4(0.55f, 0.66f, 0.85f, 1) * 0.9f,
            RimDirection = new Vector4(-toRim, 0),
            RimColour = new Vector4(0.9f, 0.95f, 1.0f, 1) * 2.2f,
            SkyColour = new Vector4(0.32f, 0.36f, 0.42f, 1),
            GroundColour = new Vector4(0.13f, 0.12f, 0.11f, 1),
            BackgroundTop = Linear(System.Windows.Media.Color.FromRgb(0x2A, 0x2D, 0x33)),
            BackgroundBottom = Linear(System.Windows.Media.Color.FromRgb(0x10, 0x11, 0x13)),
            Viewport = new Vector4(width, height, 1.0f, 1),
            Ground = new Vector4(centre.X, centre.Y, radius * 2.2f, 0),
        };
    }

    static MaterialConstants Constants(RenderMaterial? material, System.Windows.Media.Color colour, bool translucent)
    {
        var constants = new MaterialConstants
        {
            Mode = new Vector4(0, material?.Normal is null ? 0 : 1, material?.Mask is null ? 0 : 1, material?.Diffuse is null ? 0 : 1),
            BaseColour = material is null ? Linear(colour) : Vector4.One,
            Options = new Vector4(material?.AlphaTest == true ? 1 : 0, material is null ? 0.6f : 0.55f, material?.GlossInNormal == true ? 1 : 0, material?.Specular is null ? 0 : 1),
            Blend = new Vector4(translucent ? 1 : 0, 0, 0, 0),
        };

        var cloth = material?.Cloth;
        if (cloth is null)
            return constants;

        constants.Mode.X = cloth.Simplified ? 2 : 1;
        constants.BaseHsv = Vector(cloth, "1:Layer0_BaseHSV", Vector3.One);
        constants.MaskR = Vector(cloth, "1:MaskR", Vector3.One);
        constants.MaskG = Vector(cloth, "1:MaskG", Vector3.One);
        constants.MaskB = Vector(cloth, "1:MaskB", Vector3.One);
        constants.FinalHsv = Vector(cloth, "0: FinalAlbedoHSV", Vector3.One);
        constants.Camo = new Vector4(cloth.Float("Camo re-diffuse"), cloth.Float("Camo_VertexAlpha"), cloth.Simplified ? 0 : cloth.Float("Camo_UV_AB"), cloth.Float("z_DBG_NoHSV_mod"));
        constants.CamoZones = new Vector4(cloth.Float("Camo_In_Cotton"), cloth.Float("Camo_In_Velvet"), cloth.Float("Camo_In_Leather"), cloth.Float("Camo_In_Metal"));
        (constants.CamoUv0, constants.CamoUv1) = Rows(cloth.Transform("Camo_UV"));
        (constants.DetailUv0, constants.DetailUv1) = Rows(cloth.Transform("6:DetailDiffuse_UV"));
        (constants.DetailNormalUv0, constants.DetailNormalUv1) = Rows(cloth.Transform("8:DetailNormal_UV"));
        constants.UvAb = new Vector4(cloth.Float("4:DiffuseAndMask1_UV_AB"), cloth.Float("4:Normal_UV_AB"), cloth.Float("6:DetailDiffuse_UV_AB"), 0);
        constants.Reflect = new Vector4(cloth.Float("9:MetalReflectance_intensity"), cloth.Float("9:MetalReflectance_Hue"), cloth.Float("9:MetalReflectance_Saturation"), cloth.Float("9:MetalGlossiness", 1));
        constants.ClothReflect = new Vector4(cloth.Float("9:ClothReflectance_Hue"), cloth.Float("9:ClothReflectance_Saturation"), 0, 0);
        constants.Detail = new Vector4(cloth.Float("8:Detail diffuse int Cotton"), cloth.Float("8:Detail diffuse int Velvet"), 0, 0);
        constants.DetailWhite = Vector(cloth, "6:DetailDiffuse_MaskWhite", Vector3.One);
        constants.DetailBlack = Vector(cloth, "6:DetailDiffuse_MaskBlack", Vector3.Zero);
        constants.DetailNormal = new Vector4(cloth.Float("8:DetailNormal_Int_Cotton"), cloth.Float("8:DetailNormal_Int_Leather"), cloth.Float("8:DetailNormal_Int_Metal"), cloth.Float("8:DetailNormal_Int_Velvet"));
        constants.DirtColour = new Vector4(cloth.Vector("Dirt_Color", Vector3.Zero), cloth.Float("WoundBurning"));
        constants.Dirt = new Vector4(cloth.Float("Dirt initial power"), cloth.Float("Dirt Blend"), cloth.Float("Dirt_OverlayPower"), 0);
        return constants;
    }

    static Vector4 Vector(ClothParameters cloth, string name, Vector3 fallback) => new(cloth.Vector(name, fallback), 0);

    static (Vector4, Vector4) Rows(UvTransform transform)
    {
        float cos = MathF.Cos(transform.Rotation);
        float sin = MathF.Sin(transform.Rotation);
        return (new Vector4(transform.ScaleU * cos, -transform.ScaleV * sin, transform.OffsetU, 0),
            new Vector4(transform.ScaleU * sin, transform.ScaleV * cos, transform.OffsetV, 0));
    }

    ID3D11ShaderResourceView View(TexturePixels? pixels, ID3D11ShaderResourceView fallback)
    {
        if (pixels is null)
            return fallback;

        if (_textures.TryGetValue(pixels, out var known))
            return known;

        var format = pixels.Srgb ? Format.B8G8R8A8_UNorm_SRgb : Format.B8G8R8A8_UNorm;
        var texture = _device.CreateTexture2D(new Texture2DDescription(format, (uint)pixels.Width, (uint)pixels.Height, 1, 0,
            BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
        _context.UpdateSubresource(pixels.Bgra, texture, 0, (uint)(pixels.Width * 4));
        var view = _device.CreateShaderResourceView(texture);
        _context.GenerateMips(view);
        texture.Dispose();

        _textures[pixels] = view;
        return view;
    }

    public void Forget(IEnumerable<TexturePixels> pixels)
    {
        foreach (var each in pixels)
        {
            if (_textures.Remove(each, out var view))
                view.Dispose();
        }
    }

    ID3D11ShaderResourceView Solid(byte b, byte g, byte r, byte a)
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 1, 1, 1, 1, BindFlags.ShaderResource));
        _context.UpdateSubresource(new byte[] { b, g, r, a }, texture, 0, 4);
        var view = _device.CreateShaderResourceView(texture);
        texture.Dispose();
        return view;
    }

    void Resize(int width, int height)
    {
        if (width == _width && height == _height && _colour is not null)
            return;

        _colourView?.Dispose();
        _colour?.Dispose();
        _depthView?.Dispose();
        _depth?.Dispose();
        _resolved?.Dispose();
        _staging?.Dispose();

        _width = width;
        _height = height;
        _colour = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm_SRgb, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, Samples, 0));
        _colourView = _device.CreateRenderTargetView(_colour);
        _depth = _device.CreateTexture2D(new Texture2DDescription(Format.D32_Float, (uint)width, (uint)height, 1, 1, BindFlags.DepthStencil, ResourceUsage.Default, CpuAccessFlags.None, Samples, 0));
        _depthView = _device.CreateDepthStencilView(_depth);
        _resolved = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm_SRgb, (uint)width, (uint)height, 1, 1, BindFlags.None));
        _staging = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm_SRgb, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
    }

    static Vector4 Linear(System.Windows.Media.Color colour) => new(ToLinear(colour.R), ToLinear(colour.G), ToLinear(colour.B), 1);

    static float ToLinear(byte value)
    {
        float c = value / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    static Vector3 ToVector(Point3D point) => new((float)point.X, (float)point.Y, (float)point.Z);

    static Vector3 ToVector(Vector3D vector) => new((float)vector.X, (float)vector.Y, (float)vector.Z);

    static string ShaderSource()
    {
        using var stream = typeof(MeshRenderer).Assembly.GetManifestResourceStream("Wildlands.Toolkit.Preview.MeshShader.hlsl")
            ?? throw new InvalidOperationException("The mesh shader is missing from the toolkit.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static ReadOnlySpan<byte> Compile(string source, string entry, string profile, out byte[] code)
    {
        var result = Compiler.Compile(source, entry, "MeshShader.hlsl", profile, out var blob, out var errors);
        if (result.Failure || blob is null)
            throw new InvalidOperationException($"The mesh shader does not compile ({entry}): {errors?.AsString()}");

        code = blob.AsBytes();
        return code;
    }

    public void Dispose()
    {
        foreach (var view in _textures.Values)
            view.Dispose();
        _textures.Clear();

        _vertices?.Dispose();
        _indices?.Dispose();
        _overlayVertices?.Dispose();
        _overlayIndices?.Dispose();
        _groundVertices?.Dispose();
        _colourView?.Dispose();
        _colour?.Dispose();
        _depthView?.Dispose();
        _depth?.Dispose();
        _resolved?.Dispose();
        _staging?.Dispose();

        _white.Dispose();
        _black.Dispose();
        _flat.Dispose();
        _shadowView.Dispose();
        _shadowDepth.Dispose();
        _shadowTexture.Dispose();
        _frameBuffer.Dispose();
        _materialBuffer.Dispose();
        _wrap.Dispose();
        _shadowSampler.Dispose();
        _opaque.Dispose();
        _blend.Dispose();
        _depthOn.Dispose();
        _depthRead.Dispose();
        _depthOff.Dispose();
        _cullBack.Dispose();
        _cullNone.Dispose();
        _shadowRaster.Dispose();
        _layout.Dispose();
        _meshVertex.Dispose();
        _shadowVertex.Dispose();
        _backgroundVertex.Dispose();
        _meshPixel.Dispose();
        _unlitPixel.Dispose();
        _groundPixel.Dispose();
        _backgroundPixel.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FrameConstants
    {
        public Matrix4x4 ViewProjection;
        public Matrix4x4 ShadowProjection;
        public Vector4 CameraPosition;
        public Vector4 KeyDirection;
        public Vector4 KeyColour;
        public Vector4 FillDirection;
        public Vector4 FillColour;
        public Vector4 RimDirection;
        public Vector4 RimColour;
        public Vector4 SkyColour;
        public Vector4 GroundColour;
        public Vector4 BackgroundTop;
        public Vector4 BackgroundBottom;
        public Vector4 Viewport;
        public Vector4 Ground;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MaterialConstants
    {
        public Vector4 Mode;
        public Vector4 BaseColour;
        public Vector4 Options;
        public Vector4 BaseHsv;
        public Vector4 MaskR;
        public Vector4 MaskG;
        public Vector4 MaskB;
        public Vector4 FinalHsv;
        public Vector4 Camo;
        public Vector4 CamoZones;
        public Vector4 CamoUv0;
        public Vector4 CamoUv1;
        public Vector4 DetailUv0;
        public Vector4 DetailUv1;
        public Vector4 DetailNormalUv0;
        public Vector4 DetailNormalUv1;
        public Vector4 UvAb;
        public Vector4 Reflect;
        public Vector4 ClothReflect;
        public Vector4 Detail;
        public Vector4 DetailWhite;
        public Vector4 DetailBlack;
        public Vector4 DetailNormal;
        public Vector4 DirtColour;
        public Vector4 Dirt;
        public Vector4 Blend;
    }
}
