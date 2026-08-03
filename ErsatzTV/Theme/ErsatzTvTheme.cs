using MudBlazor;

namespace ErsatzTV.Theme;

public static class ErsatzTvTheme
{
    private static readonly string[] FontFamily = ["Roboto", "Helvetica", "Arial", "sans-serif"];

    public static MudTheme Create() => new()
    {
        PaletteDark = new PaletteDark
        {
            Black = "#000000",
            White = Colors.Shades.White,
            Primary = "#7DDA7F",
            PrimaryContrastText = "#003910",
            Secondary = "#4DD0C8",
            SecondaryContrastText = "#003732",
            Tertiary = "#A8D48A",
            TertiaryContrastText = "#163800",
            Info = "#5EC8D0",
            Success = "#7DDA7F",
            Warning = "#F0B060",
            Error = "#FFB4AB",
            Dark = "#101410",
            Background = "#101410",
            BackgroundGray = "#1A211C",
            Surface = "#1A211C",
            AppbarBackground = "#101410",
            AppbarText = "rgba(230, 236, 230, 0.92)",
            DrawerBackground = "#141A16",
            DrawerText = "rgba(230, 236, 230, 0.92)",
            DrawerIcon = "rgba(230, 236, 230, 0.80)",
            TextPrimary = "rgba(230, 236, 230, 0.95)",
            TextSecondary = "rgba(198, 208, 198, 0.78)",
            TextDisabled = "rgba(198, 208, 198, 0.38)",
            ActionDefault = "rgba(230, 236, 230, 0.80)",
            ActionDisabled = "rgba(198, 208, 198, 0.38)",
            ActionDisabledBackground = "rgba(230, 236, 230, 0.12)",
            Divider = "rgba(198, 208, 198, 0.16)",
            DividerLight = "rgba(198, 208, 198, 0.08)",
            TableLines = "rgba(198, 208, 198, 0.12)",
            TableStriped = "rgba(125, 218, 127, 0.04)",
            TableHover = "rgba(125, 218, 127, 0.10)",
            LinesDefault = "rgba(198, 208, 198, 0.14)",
            LinesInputs = "rgba(198, 208, 198, 0.28)",
            OverlayDark = "rgba(16, 20, 16, 0.55)",
            OverlayLight = "rgba(230, 236, 230, 0.08)"
        },
        PaletteLight = new PaletteLight
        {
            Black = "#000000",
            White = Colors.Shades.White,
            Primary = "#1B6B2E",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#00695C",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#4A6B1F",
            TertiaryContrastText = "#FFFFFF",
            Info = "#007A84",
            Success = "#1B6B2E",
            Warning = "#8B5A00",
            Error = "#BA1A1A",
            Dark = "#1A211C",
            Background = "#F4F7F4",
            BackgroundGray = "#E8F0E9",
            Surface = "#FFFFFF",
            AppbarBackground = "#E8F0E9",
            AppbarText = "#1A211C",
            DrawerBackground = "#F4F7F4",
            DrawerText = "#1A211C",
            DrawerIcon = "#3D4A40",
            TextPrimary = "#1A211C",
            TextSecondary = "rgba(26, 33, 28, 0.72)",
            TextDisabled = "rgba(26, 33, 28, 0.38)",
            ActionDefault = "#3D4A40",
            ActionDisabled = "rgba(26, 33, 28, 0.30)",
            ActionDisabledBackground = "rgba(26, 33, 28, 0.10)",
            Divider = "rgba(26, 33, 28, 0.12)",
            DividerLight = "rgba(26, 33, 28, 0.06)",
            TableLines = "rgba(26, 33, 28, 0.10)",
            TableStriped = "rgba(27, 107, 46, 0.04)",
            TableHover = "rgba(27, 107, 46, 0.08)",
            LinesDefault = "rgba(26, 33, 28, 0.12)",
            LinesInputs = "rgba(26, 33, 28, 0.28)",
            OverlayDark = "rgba(26, 33, 28, 0.45)",
            OverlayLight = "rgba(255, 255, 255, 0.55)"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "16px",
            DrawerWidthLeft = "288px",
            DrawerWidthRight = "320px",
            DrawerMiniWidthLeft = "80px",
            AppbarHeight = "72px"
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = FontFamily,
                FontSize = ".9375rem",
                FontWeight = "400",
                LineHeight = "1.5",
                LetterSpacing = ".01em"
            },
            H1 = new H1Typography
            {
                FontFamily = FontFamily,
                FontSize = "3.25rem",
                FontWeight = "700",
                LineHeight = "1.15",
                LetterSpacing = "-.02em"
            },
            H2 = new H2Typography
            {
                FontFamily = FontFamily,
                FontSize = "2.5rem",
                FontWeight = "700",
                LineHeight = "1.2",
                LetterSpacing = "-.015em"
            },
            H3 = new H3Typography
            {
                FontFamily = FontFamily,
                FontSize = "2rem",
                FontWeight = "600",
                LineHeight = "1.25",
                LetterSpacing = "-.01em"
            },
            H4 = new H4Typography
            {
                FontFamily = FontFamily,
                FontSize = "1.75rem",
                FontWeight = "600",
                LineHeight = "1.3",
                LetterSpacing = "-.01em"
            },
            H5 = new H5Typography
            {
                FontFamily = FontFamily,
                FontSize = "1.375rem",
                FontWeight = "600",
                LineHeight = "1.35",
                LetterSpacing = "0"
            },
            H6 = new H6Typography
            {
                FontFamily = FontFamily,
                FontSize = "1.125rem",
                FontWeight = "600",
                LineHeight = "1.4",
                LetterSpacing = ".01em"
            },
            Subtitle1 = new Subtitle1Typography
            {
                FontFamily = FontFamily,
                FontSize = "1rem",
                FontWeight = "500",
                LineHeight = "1.5",
                LetterSpacing = ".01em"
            },
            Subtitle2 = new Subtitle2Typography
            {
                FontFamily = FontFamily,
                FontSize = ".875rem",
                FontWeight = "500",
                LineHeight = "1.45",
                LetterSpacing = ".01em"
            },
            Body1 = new Body1Typography
            {
                FontFamily = FontFamily,
                FontSize = ".9375rem",
                FontWeight = "400",
                LineHeight = "1.55",
                LetterSpacing = ".01em"
            },
            Body2 = new Body2Typography
            {
                FontFamily = FontFamily,
                FontSize = ".8125rem",
                FontWeight = "400",
                LineHeight = "1.5",
                LetterSpacing = ".01em"
            },
            Button = new ButtonTypography
            {
                FontFamily = FontFamily,
                FontSize = ".875rem",
                FontWeight = "600",
                LineHeight = "1.25",
                LetterSpacing = ".02em",
                TextTransform = "none"
            },
            Caption = new CaptionTypography
            {
                FontFamily = FontFamily,
                FontSize = ".75rem",
                FontWeight = "500",
                LineHeight = "1.35",
                LetterSpacing = ".02em"
            },
            Overline = new OverlineTypography
            {
                FontFamily = FontFamily,
                FontSize = ".6875rem",
                FontWeight = "600",
                LineHeight = "1.4",
                LetterSpacing = ".06em",
                TextTransform = "uppercase"
            }
        },
        Shadows = new Shadow
        {
            Elevation =
            [
                "none",
                "0 1px 2px rgba(0, 0, 0, 0.18), 0 1px 3px 1px rgba(0, 0, 0, 0.10)",
                "0 1px 2px rgba(0, 0, 0, 0.20), 0 2px 6px 2px rgba(0, 0, 0, 0.12)",
                "0 1px 3px rgba(0, 0, 0, 0.22), 0 4px 8px 3px rgba(0, 0, 0, 0.12)",
                "0 2px 3px rgba(0, 0, 0, 0.22), 0 6px 10px 4px rgba(0, 0, 0, 0.12)",
                "0 4px 4px rgba(0, 0, 0, 0.22), 0 8px 12px 6px rgba(0, 0, 0, 0.12)",
                "0px 3px 5px -1px rgba(0,0,0,0.2),0px 6px 10px 0px rgba(0,0,0,0.14),0px 1px 18px 0px rgba(0,0,0,0.12)",
                "0px 4px 5px -2px rgba(0,0,0,0.2),0px 7px 10px 1px rgba(0,0,0,0.14),0px 2px 16px 1px rgba(0,0,0,0.12)",
                "0px 5px 5px -3px rgba(0,0,0,0.2),0px 8px 10px 1px rgba(0,0,0,0.14),0px 3px 14px 2px rgba(0,0,0,0.12)",
                "0px 5px 6px -3px rgba(0,0,0,0.2),0px 9px 12px 1px rgba(0,0,0,0.14),0px 3px 16px 2px rgba(0,0,0,0.12)",
                "0px 6px 6px -3px rgba(0,0,0,0.2),0px 10px 14px 1px rgba(0,0,0,0.14),0px 4px 18px 3px rgba(0,0,0,0.12)",
                "0px 6px 7px -4px rgba(0,0,0,0.2),0px 11px 15px 1px rgba(0,0,0,0.14),0px 4px 20px 3px rgba(0,0,0,0.12)",
                "0px 7px 8px -4px rgba(0,0,0,0.2),0px 12px 17px 2px rgba(0,0,0,0.14),0px 5px 22px 4px rgba(0,0,0,0.12)",
                "0px 7px 8px -4px rgba(0,0,0,0.2),0px 13px 19px 2px rgba(0,0,0,0.14),0px 5px 24px 4px rgba(0,0,0,0.12)",
                "0px 7px 9px -4px rgba(0,0,0,0.2),0px 14px 21px 2px rgba(0,0,0,0.14),0px 5px 26px 4px rgba(0,0,0,0.12)",
                "0px 8px 9px -5px rgba(0,0,0,0.2),0px 15px 22px 2px rgba(0,0,0,0.14),0px 6px 28px 5px rgba(0,0,0,0.12)",
                "0px 8px 10px -5px rgba(0,0,0,0.2),0px 16px 24px 2px rgba(0,0,0,0.14),0px 6px 30px 5px rgba(0,0,0,0.12)",
                "0px 8px 11px -5px rgba(0,0,0,0.2),0px 17px 26px 2px rgba(0,0,0,0.14),0px 6px 32px 5px rgba(0,0,0,0.12)",
                "0px 9px 11px -5px rgba(0,0,0,0.2),0px 18px 28px 2px rgba(0,0,0,0.14),0px 7px 34px 6px rgba(0,0,0,0.12)",
                "0px 9px 12px -6px rgba(0,0,0,0.2),0px 19px 29px 2px rgba(0,0,0,0.14),0px 7px 36px 6px rgba(0,0,0,0.12)",
                "0px 10px 13px -6px rgba(0,0,0,0.2),0px 20px 31px 3px rgba(0,0,0,0.14),0px 8px 38px 7px rgba(0,0,0,0.12)",
                "0px 10px 13px -6px rgba(0,0,0,0.2),0px 21px 33px 3px rgba(0,0,0,0.14),0px 8px 40px 7px rgba(0,0,0,0.12)",
                "0px 10px 14px -6px rgba(0,0,0,0.2),0px 22px 35px 3px rgba(0,0,0,0.14),0px 8px 42px 7px rgba(0,0,0,0.12)",
                "0px 11px 14px -7px rgba(0,0,0,0.2),0px 23px 36px 3px rgba(0,0,0,0.14),0px 9px 44px 8px rgba(0,0,0,0.12)",
                "0px 11px 15px -7px rgba(0,0,0,0.2),0px 24px 38px 3px rgba(0,0,0,0.14),0px 9px 46px 8px rgba(0,0,0,0.12)",
                "0 12px 28px rgba(0, 0, 0, 0.28), 0 16px 40px rgba(0, 0, 0, 0.18)"
            ]
        }
    };
}
