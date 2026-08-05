namespace TruthOrDare.Models;

[System.Flags]
public enum CardCategory
{
    None = 0,
    Sfw = 1 << 0,
    Mixed = 1 << 1,
    Nsfw = 1 << 2,
    NsfwPlus = 1 << 3,
}
