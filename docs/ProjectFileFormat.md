# Format du fichier `.crproj`

Un projet Crowbar est persisté dans un fichier JSON versionné, lisible par un
humain et diff-friendly en contrôle de version. Le conteneur est le DTO
`CrowbarProjectData` (`src/Engine/Project/CrowbarProjectData.cs`), écrit par
`CrowbarProjectSerializer.Serialize` et relu par
`CrowbarProjectSerializer.Deserialize`. L'asset est manipulé par
`CrowbarProjectFile` (`src/Engine/Project/CrowbarProjectFile.cs`), qui
sauvegarde de façon atomique : écriture dans un fichier `.tmp` puis déplacement
par-dessus la destination.

Le fichier se trouve **à la racine du répertoire projet qu'il décrit** : ce
répertoire <em>est</em> le projet. Le contenu du projet (niveaux `.level`, code
du projet de jeu, assets) vit à côté du `.crproj`.

## Exemple

```json
{
  "format": 1,
  "id": "8f4d2a1e-9c2b-4e5d-a1b2-3c4d5e6f7081",
  "name": "MyGame",
  "version": "0.1",
  "author": "Jane Doe",
  "description": "Un jeu de démonstration Crowbar."
}
```

## Champs

| Champ | Type | Rôle |
|---|---|---|
| `format` | int | Version du format. `CrowbarProjectFile.CurrentFormat` (1 aujourd'hui). Un fichier plus récent que le build **refuse** de charger ; un fichier plus ancien charge avec un warning (point de migration). |
| `id` | string (GUID) | Identité stable du projet, préservée à travers save/load (références externes : lanceur, scripts de build). |
| `name` | string | Nom d'affichage du projet (affiché dans la barre de titre de l'éditeur). |
| `version` | string | Version du projet lui-même (version produit, pas celle de l'éditeur). |
| `author` | string \| null | Auteur du projet, ou absent. |
| `description` | string \| null | Courte description du projet, ou absente. |

## Chargement

- **Double clic / ligne de commande** : l'éditeur reçoit le chemin du `.crproj`
  en premier argument (`Crowbar.Editor.exe "C:\Projets\MyGame\MyGame.crproj"`).
  Il le lit via `CrowbarProjectFile.LoadFromDisk` (avant toute configuration de
  filesystem) puis **racine le filesystem projet sur le répertoire du fichier**,
  afin que niveaux et code du projet de jeu soient sauvegardés à côté du projet.
- **Depuis l'éditeur** : le bouton « dossier » de la barre d'outils ouvre la
  fenêtre Explorateur Windows (dialogue natif `GetOpenFileName`) pour choisir un
  `.crproj` ; l'éditeur recale alors le filesystem projet, recharge le niveau et
  recompile le projet de jeu du nouveau projet.
- **Démarrage à nu** (sans argument) : l'éditeur retombe sur le projet de démo
  (répertoire `Game/`), sans fichier projet.

## Contrat de compatibilité

Comme le format `.level`, un `format` **plus récent** que le build est un échec
franc (`InvalidDataException`) : on préfère un message clair à une corruption
silencieuse. Un format plus ancien charge avec un warning.

## Notes

- L'écriture est atomique (`.tmp` → déplacement), donc un crash ne laisse jamais
  de fichier tronqué.
- L'association `.crproj` → éditeur sous Windows s'enregistre avec
  `tools\RegisterCrowbarProject.ps1` (registre HKCU, sans droits admin).