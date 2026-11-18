using QRCoder;

namespace TransubPlayer.Services;

internal static class LanQrEncoder
{
    public static byte[] PngBytes(string payload, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }

    public static string DataUrl(string payload, int pixelsPerModule = 6)
    {
        var bytes = PngBytes(payload, pixelsPerModule);
        return "data:image/png;base64," + Convert.ToBase64String(bytes);
    }
}
