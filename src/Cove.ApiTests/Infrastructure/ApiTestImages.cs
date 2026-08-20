namespace Cove.ApiTests.Infrastructure;

public static class ApiTestImages
{
    private const string OnePixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==";
    private const string RedPixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    private const string BluePixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYPj/HwADAgH/5ncLrgAAAABJRU5ErkJggg==";

    public static byte[] OnePixelPng()
        => Convert.FromBase64String(OnePixelPngBase64);

    public static byte[] RedPixelPng()
        => Convert.FromBase64String(RedPixelPngBase64);

    public static byte[] BluePixelPng()
        => Convert.FromBase64String(BluePixelPngBase64);
}
