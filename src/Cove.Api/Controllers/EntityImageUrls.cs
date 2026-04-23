namespace Cove.Api.Controllers;

internal static class EntityImageUrls
{
    public const int DefaultEntityImageMaxDimension = 640;
    public const int DefaultGalleryCoverMaxDimension = 640;

    public static string Performer(int id, DateTime updatedAt, int maxDimension = DefaultEntityImageMaxDimension)
        => Build($"/api/performers/{id}/image", updatedAt, maxDimension);

    public static string Studio(int id, DateTime updatedAt, int maxDimension = DefaultEntityImageMaxDimension)
        => Build($"/api/studios/{id}/image", updatedAt, maxDimension);

    public static string Tag(int id, DateTime updatedAt, int maxDimension = DefaultEntityImageMaxDimension)
        => Build($"/api/tags/{id}/image", updatedAt, maxDimension);

    public static string GroupFront(int id, DateTime updatedAt, int maxDimension = DefaultEntityImageMaxDimension)
        => Build($"/api/groups/{id}/image/front", updatedAt, maxDimension);

    public static string GroupBack(int id, DateTime updatedAt, int maxDimension = DefaultEntityImageMaxDimension)
        => Build($"/api/groups/{id}/image/back", updatedAt, maxDimension);

    public static string GalleryCover(int id, DateTime updatedAt, int maxDimension = DefaultGalleryCoverMaxDimension)
        => Build($"/api/galleries/{id}/cover", updatedAt, maxDimension);

    private static string Build(string path, DateTime updatedAt, int maxDimension)
        => $"{path}?max={maxDimension}&v={Uri.EscapeDataString(updatedAt.ToString("o"))}";
}