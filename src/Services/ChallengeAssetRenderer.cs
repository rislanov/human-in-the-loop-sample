using System.Net;
using System.Security.Cryptography;

namespace HumanLoopBooking.Services;

public sealed class ChallengeAssetRenderer
{
    public string RenderBackground(ChallengeSession challenge)
    {
        var palette = PaletteFor(challenge.Id);
        var path = PuzzlePath(challenge.TargetX, challenge.PieceY);

        return $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{{challenge.Width}}" height="{{challenge.Height}}" viewBox="0 0 {{challenge.Width}} {{challenge.Height}}">
              <defs>
                <linearGradient id="sky" x1="0" x2="1" y1="0" y2="1">
                  <stop offset="0" stop-color="{{palette.Light}}" />
                  <stop offset="1" stop-color="{{palette.Wash}}" />
                </linearGradient>
                <pattern id="grid" width="28" height="28" patternUnits="userSpaceOnUse">
                  <path d="M 28 0 L 0 0 0 28" fill="none" stroke="{{palette.Line}}" stroke-width="1" opacity=".38" />
                </pattern>
                <filter id="softShadow" x="-20%" y="-20%" width="140%" height="140%">
                  <feDropShadow dx="0" dy="5" stdDeviation="5" flood-color="#123047" flood-opacity=".22" />
                </filter>
              </defs>
              <rect width="100%" height="100%" rx="8" fill="url(#sky)" />
              <rect width="100%" height="100%" rx="8" fill="url(#grid)" />
              <circle cx="58" cy="48" r="31" fill="{{palette.Teal}}" opacity=".74" />
              <circle cx="306" cy="44" r="34" fill="{{palette.Gold}}" opacity=".72" />
              <circle cx="286" cy="136" r="28" fill="{{palette.Rose}}" opacity=".68" />
              <path d="M18 136 C76 84, 112 190, 181 119 S291 72, 342 122" fill="none" stroke="{{palette.Navy}}" stroke-width="12" stroke-linecap="round" opacity=".24" />
              <path d="M24 78 C93 113, 137 18, 206 54 S284 104, 344 69" fill="none" stroke="#ffffff" stroke-width="8" stroke-linecap="round" opacity=".44" />
              <path d="{{path}}" fill="#f9fbfd" fill-opacity=".9" stroke="#193c57" stroke-opacity=".68" stroke-width="2" stroke-dasharray="5 5" filter="url(#softShadow)" />
              <path d="{{path}}" fill="none" stroke="#ffffff" stroke-width="4" stroke-opacity=".55" />
            </svg>
            """;
    }

    public string RenderPiece(ChallengeSession challenge)
    {
        var palette = PaletteFor(challenge.Id);
        var path = PuzzlePath(8, 12);

        return $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{{challenge.PieceSize + 16}}" height="{{challenge.PieceSize + 22}}" viewBox="0 0 {{challenge.PieceSize + 16}} {{challenge.PieceSize + 22}}">
              <defs>
                <linearGradient id="pieceFill" x1="0" x2="1" y1="0" y2="1">
                  <stop offset="0" stop-color="{{palette.Gold}}" />
                  <stop offset=".55" stop-color="{{palette.Teal}}" />
                  <stop offset="1" stop-color="{{palette.Navy}}" />
                </linearGradient>
                <filter id="pieceShadow" x="-35%" y="-35%" width="170%" height="170%">
                  <feDropShadow dx="0" dy="6" stdDeviation="4" flood-color="#0f2434" flood-opacity=".28" />
                </filter>
              </defs>
              <path d="{{path}}" fill="url(#pieceFill)" stroke="#ffffff" stroke-width="2.5" filter="url(#pieceShadow)" />
              <path d="M17 32 h28" stroke="#ffffff" stroke-width="3" stroke-linecap="round" opacity=".72" />
              <path d="M20 42 h20" stroke="#ffffff" stroke-width="3" stroke-linecap="round" opacity=".52" />
            </svg>
            """;
    }

    private static string PuzzlePath(int x, int y)
    {
        return FormattableString.Invariant(
            $"M{x} {y} h18 c0 -8 12 -8 12 0 h18 v18 c8 0 8 12 0 12 v18 h-48 v-18 c-8 0 -8 -12 0 -12 z");
    }

    private static Palette PaletteFor(string challengeId)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(challengeId));
        var palettes = new[]
        {
            new Palette("#e8f3f1", "#f7fbff", "#10a68e", "#f0bb4c", "#d56a74", "#153b59", "#9bb4c4"),
            new Palette("#eef4fb", "#fbfaf4", "#3f8fc5", "#e3b448", "#c66889", "#22344d", "#adc1c9"),
            new Palette("#f0f6ed", "#f8fbff", "#2ba87f", "#e4a747", "#cf6d5f", "#263d58", "#a7b8aa")
        };

        return palettes[bytes[0] % palettes.Length];
    }

    private sealed record Palette(string Light, string Wash, string Teal, string Gold, string Rose, string Navy, string Line);
}
