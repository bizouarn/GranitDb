namespace GranitDb.Client.Components.Dialogs;

/// <summary>Ce que le dialogue de domaine rend en se fermant : le domaine et ce qu'il a changé, dit en une phrase.</summary>
public sealed record DomainSaved(string Id, string Summary);
