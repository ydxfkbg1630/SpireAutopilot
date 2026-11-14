using Godot;
namespace SpireAutopilot;
internal static class AutopilotTheme
{
    public static readonly Color Ink = new("#201c29"), Slate = new("#302b38"), Gold = new("#a68b56"), Paper = new("#e9dfc6"), Muted = new("#b0a797"), Amber = new("#d9a44c"), Model = new("#8bb5b3");
    public static StyleBoxFlat Panel(int padding = 13) => new()
    {
        BgColor = new Color(Ink, .97f), BorderColor = new Color(Gold, .72f),
        BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
        CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding
    };
    public static Theme Create()
    {
        var theme = new Theme { DefaultFont = new SystemFont { FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" } }, DefaultFontSize = 17 };
        theme.SetColor("font_color", "Label", Paper); theme.SetColor("font_color", "Button", Paper); theme.SetColor("font_color", "CheckBox", Paper);
        theme.SetStylebox("normal", "Button", ButtonStyle(Slate)); theme.SetStylebox("hover", "Button", ButtonStyle(new Color("#413747"))); theme.SetStylebox("pressed", "Button", ButtonStyle(new Color("#51422f")));
        return theme;
    }
    private static StyleBoxFlat ButtonStyle(Color color) => new() { BgColor = color, BorderColor = new Color(Gold, .45f), BorderWidthBottom = 1, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6, CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3 };
}
