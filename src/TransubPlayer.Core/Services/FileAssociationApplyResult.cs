namespace TransubPlayer.Services;

internal sealed record FileAssociationApplyResult(int Succeeded, int Failed, string? LastError);
