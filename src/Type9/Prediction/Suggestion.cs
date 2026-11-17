namespace Type9.Prediction;

public enum SuggestionKind { Completion, NextWord, Abbreviation }

/// <summary>
/// Вариант подсказки. <see cref="Backspaces"/> символов стирается, затем печатается <see cref="Insert"/>.
/// <see cref="Typed"/>/<see cref="Rest"/> — для отображения (набранная часть приглушена).
/// </summary>
public sealed record Suggestion(SuggestionKind Kind, string Typed, string Rest, string Insert, int Backspaces)
{
    public string Display => Typed + Rest;
}
