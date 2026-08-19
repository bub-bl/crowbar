# Format du fichier `.level`

Un niveau (`Level`) est persisté dans un fichier JSON versionné, lisible par un
humain et diff-friendly en contrôle de version. Le conteneur est le DTO
`LevelFileData` (`src/Engine/World/LevelFileData.cs`), écrit par
`LevelSerializer.Serialize` et relu par `LevelSerializer.Deserialize` +
`CreateLevel`. L'asset est manipulé par `LevelFile` (`src/Engine/World/LevelFile.cs`),
qui sauvegarde dans le filesystem projet (`FileSystem.Project`) de façon
atomique : écriture dans un fichier `.tmp` puis `MoveFile` par-dessus la
destination.

## Exemple

```json
{
  "format": 1,
  "id": "8f4d2a1e-...",
  "metadata": { "name": "Demo", "version": "0.1" },
  "entities": [
    {
      "id": "c1b0...",
      "name": "Cube",
      "components": [
        {
          "type": "MeshRenderer",
          "transform": "0,0.5,0,0.259,0.122,0.951,0.065,1,1,1",
          "properties": {
            "Model": { "procedural": "cube" },
            "Material": {
              "shader": "Surface/StandardPbr",
              "technique": "Main",
              "values": {
                "color": [0.2, 0.6, 1.0, 1.0],
                "metallic": 0.15,
                "roughness": 0.45,
                "occlusion": 1,
                "emissive": 0
              },
              "blendMode": "Opaque",
              "doubleSided": false
            }
          }
        }
      ]
    }
  ],
  "attachments": [
    { "parent": "c1b0...", "child": "d2c1..." }
  ]
}
```

## Champs

| Champ | Type | Rôle |
|---|---|---|
| `format` | int | Version du format. `LevelFile.CurrentFormat` (1 aujourd'hui). Un fichier plus récent que le build **refuse** de charger ; un fichier plus ancien charge avec un warning (point de migration). |
| `id` | string (GUID) | Identité stable du niveau, préservée à travers save/load. |
| `metadata` | objet | `name` (nom affiché du niveau) et `version` (version de l'éditeur qui a écrit le fichier). |
| `entities` | tableau | Les entités du niveau, dans l'ordre de spawn. |
| `attachments` | tableau | Relations parent → enfant entre transformes, résolues **en seconde phase** après création de toutes les entités (ordre du fichier non contraint). |

### Entité

| Champ | Rôle |
|---|---|
| `id` | GUID stable de l'entité, restauré au load (jamais régénéré : les références externes survivent). |
| `name` | Nom d'affichage. |
| `components` | Les composants attachés. |

### Composant

| Champ | Rôle |
|---|---|
| `type` | Nom court du type (ex. `MeshRenderer`, `PointLight`). Résolu via `ComponentTypeRegistry` au load. |
| `transform` | Transforme locale des composants spatiaux (`TransformComponent`), au format canonique `px,py,pz,rx,ry,rz,rw,sx,sy,sz` (culture invariante). Absent pour les composants purement logiques. |
| `properties` | Valeurs des propriétés publiques **écrivables** marquées `[Property]` (le même contrat réflexif que l'inspecteur). Les propriétés en lecture seule (valeurs dérivées, ex. `DirectionalLight.Direction`) ne sont pas persistées : elles se recalculent. |

### Valeurs de propriétés

Les valeurs sont écrites en forme JSON native, culture-invariante :

| Type CLR | Forme JSON |
|---|---|
| `float`, `double`, `int`, `uint`, `bool`, `string` | primitive |
| `Vector2` / `Vector3` / `Vector4` | tableau de nombres |
| `Transform` | chaîne canonique |
| `Material` | objet `{ shader, technique, values, blendMode, doubleSided }` — `values` est un objet nom de paramètre → valeur (validé contre la réflexion du shader au load) |
| `Model` | objet `{ "path": "..." }` pour un modèle fichier, ou `{ "procedural": "cube" \| "plane" }` pour une primitive |
| `enum` | nom du membre |

## Contrat de compatibilité (forward compatibility)

La lecture est tolérante — un niveau ne casse jamais au chargement à cause d'un
contenu inconnu :

- un `type` de composant inconnu (renommé, supprimé) est **ignoré avec un warning** ;
- une propriété inconnue, en lecture seule ou non restaurable est ignorée avec un warning ;
- un matériau dont un paramètre n'existe plus dans le shader est ignoré ;
- un modèle introuvable est ignoré (la propriété reste `null`).

En revanche, un `format` **plus récent** que le build est un échec franc
(`InvalidDataException`) : on préfère un message clair à une corruption
silencieuse.

## Notes

- Les entités hors niveau (world-only, ex. la caméra de l'éditeur) ne sont jamais sérialisées.
- Un composant en double dans une entité est ignoré avec un warning (une entité n'autorise qu'un composant par type).
- Les textures d'un matériau ne sont pas persistées en v1 (elles proviennent de l'import du modèle).
- L'écriture est atomique (`écriture .tmp` → `MoveFile`), donc un crash ne laisse jamais de fichier tronqué.
