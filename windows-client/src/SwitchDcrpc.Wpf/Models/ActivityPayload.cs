namespace SwitchDcrpc.Wpf.Models;

public sealed record ActivityPayload(
    string Details,
    string State,
    long StartUnix,
    string? Name = null,
    string? LargeImage = null,
    string? LargeText = null,
    string? SmallImage = null,
    string? SmallText = null,
    string? Button1Label = null,
    string? Button1Url = null
);
