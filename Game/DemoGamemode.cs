// Gamemode de démonstration : un script hébergé par Crowbar.Engine.Scripting.ScriptHost
// et surveillé depuis le dossier Game/. Éditez ce fichier pendant que l'éditeur
// tourne :
//   - changer le corps d'une méthode → hot reload IL (fast path), l'instance
//     conserve son identité et ses états ;
//   - ajouter/retirer un champ ou une méthode → full reload, états migrés.
// La barre de statut affiche Describe() en direct et une notification apparaît
// à chaque rechargement.

namespace Game;

/// <summary>État du gamemode de démo ; les champs survivent aux rechargements.</summary>
public sealed class DemoGamemode
{
    public static int ReloadCount;

    public int Score = 5;
    public string? Name = "Démo";

    /// <summary>Ligne d'état affichée dans la barre de statut de l'éditeur.</summary>
    public string Describe() => $"Gamemode {Name} : {Score} points, {ReloadCount} recharges";

    public void Bump() => Score++;
}
