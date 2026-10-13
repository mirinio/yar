using NGettext;

namespace Yar.App;

/// <summary>Translations. Every string the user can see goes through here.</summary>
internal static class T
{
    private static readonly Catalog Catalog = new Catalog("yar", Path.Combine(AppContext.BaseDirectory, "locale"));

    public static string _(string text) => Catalog.GetString(text);

    public static string F(string format, params object[] arguments) => Catalog.GetString(format, arguments);
}
