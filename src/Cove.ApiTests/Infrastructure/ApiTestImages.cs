namespace Cove.ApiTests.Infrastructure;

public static class ApiTestImages
{
    private const string OnePixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+XhZ8AAAAAElFTkSuQmCC";

    public static byte[] OnePixelPng()
        => Convert.FromBase64String(OnePixelPngBase64);
}
