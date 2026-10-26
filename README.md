# JellyShelf — lecteur de métadonnées Calibre pour Jellyfin

Plugin de métadonnées pour Jellyfin qui lit une base **Calibre `metadata.db` en lecture seule**
et enrichit la bibliothèque de livres avec les **genres, tags, auteurs, séries, titres et couvertures** —
**sans déplacer ni modifier les fichiers Calibre**, en conservant les chemins d'origine.

## Comment ça marche

Jellyfin scanne un dossier de bibliothèque de type *Livres*. Pour chaque livre, le plugin :

1. Remonte l'arborescence depuis le dossier du livre pour localiser `metadata.db`
   (ou utilise le chemin configuré).
2. Fait correspondre le livre via `books.path` (le chemin relatif du dossier, ex.
   `Jane Austen/Pride and Prejudice (123)`) — c'est exactement ce que Calibre stocke.
3. Lit titre, tri, auteurs, série + index, tags, éditeur, note, résumé (commentaire HTML),
   langues et identifiants (ISBN, Amazon, Google).
4. Fournit la couverture depuis `cover.jpg` présent dans le dossier du livre.

La base est ouverte avec `Mode=ReadOnly` ; aucune écriture n'est jamais effectuée.

### Navigation par genre / auteur / série

Une fois les métadonnées renseignées, la navigation est **native** dans Jellyfin :
filtres et tris par **genre**, **tag**, **auteur** (personnes) et **année**. Les **séries**
apparaissent avec leur nom et leur index (tri/filtre). Calibre n'ayant pas de champ
« genre » dédié, les **tags** sont mappés vers les **genres** Jellyfin par défaut
(configurable).

## Prérequis

- .NET 10 SDK pour compiler.
- Jellyfin **12.x** (voir ci-dessous pour cibler une autre version).

## Compilation

```powershell
dotnet restore
dotnet build -c Release
```

Le plugin est produit dans `bin/Release/net10.0/Jellyfin.Plugin.JellyShelf.dll`.

### Cibler une autre version de Jellyfin

Dans `Jellyfin.Plugin.JellyShelf.csproj`, ajustez la version de `Jellyfin.Controller`
pour **qu'elle corresponde à votre serveur** (visible dans *Tableau de bord → À propos*) :

| Serveur Jellyfin | `Jellyfin.Controller` | `TargetFramework` |
|------------------|-----------------------|-------------------|
| 12.x             | 12.x                  | net10.0           |
| 10.11.x          | 10.11.x               | net8.0            |
| 10.10.x          | 10.10.x               | net8.0            |

> Projet configuré par défaut pour **Jellyfin 12.3** (`Jellyfin.Controller` 12.3.0, .NET 10).

## Installation A — manuelle (sideload)

1. Copier `Jellyfin.Plugin.JellyShelf.dll` dans un sous-dossier de `plugins`, p. ex. :
   - Windows : `%ProgramData%\Jellyfin\Server\plugins\JellyShelf\`
   - Linux : `/var/lib/jellyfin/plugins/JellyShelf/`
   - Docker : `/config/plugins/JellyShelf/`
2. Redémarrer Jellyfin.
3. *Tableau de bord → Mes plugins → JellyShelf* pour la configuration.

## Installation B — via un dépôt (page Extensions / « Gérer les dépôts »)

### 1. Packager

```powershell
pwsh ./package.ps1 -BaseUrl "<URL publique du dossier qui hébergera les fichiers>"
```

Cela produit dans `./dist` :
- `jellyfin-plugin-jellyshelf_<version>.zip` (DLL + `meta.json`)
- `manifest.json` (le fichier de dépôt, contenant l'empreinte MD5 et le `sourceUrl`)

`-BaseUrl` est l'URL du **dossier** où tu publieras les deux fichiers ; le script en
déduit `sourceUrl = <BaseUrl>/<zip>`.

### 2. Héberger

Mettre `manifest.json` **et** le `.zip` à cette même URL, accessible par le serveur
Jellyfin. Options courantes :
- **Synology Web Station** : publier le dossier `dist` sur un virtual host HTTP du NAS,
  p. ex. `http://<ip-nas>:8080/jellyfin-plugins/`.
- **GitHub** : commiter `manifest.json` dans le repo (URL « raw ») et joindre le `.zip`
  à une *Release* ; dans ce cas, utiliser l'URL de la release comme `-BaseUrl`.

### 3. Ajouter le dépôt dans Jellyfin

*Tableau de bord → Plugins → Dépôts (Gérer les dépôts) → Nouveau dépôt* :
- **Nom du dépôt** : libellé libre (ex. `Calibre (local)`).
- **URL du dépôt** : l'URL du **`manifest.json`** (ex. `http://<ip-nas>:8080/jellyfin-plugins/manifest.json`).

Puis *Extensions → Catalogue → JellyShelf → Installer*, et redémarrer Jellyfin.

> À chaque nouvelle version : incrémenter `AssemblyVersion`/`FileVersion` dans le `.csproj`,
> relancer `package.ps1` avec le même `-BaseUrl`, republier les fichiers. Jellyfin proposera
> la mise à jour automatiquement.

## Configuration de la bibliothèque

1. Ajouter une bibliothèque de type **Livres** pointant sur la **racine de la bibliothèque
   Calibre** (le dossier contenant `metadata.db`).
2. Dans *Paramètres de métadonnées* de la bibliothèque, laisser le lecteur de métadonnées
   **Calibre** activé et en **priorité haute** (il est fourni avec `Order = 0`).
3. Lancer une analyse. (Facultatif) renseigner explicitement le chemin Calibre dans la page
   du plugin si la détection automatique ne suffit pas.

## Options du plugin

| Option | Défaut | Rôle |
|--------|--------|------|
| Chemin de la bibliothèque Calibre | *(vide)* | Dossier de `metadata.db`. Vide = détection auto. |
| Mapper les tags vers les genres | activé | Tags Calibre → genres Jellyfin. |
| Conserver aussi les tags | activé | Garde les tags Calibre comme tags Jellyfin. |
| Importer la série | activé | Nom de série + index. |
| Importer l'éditeur | activé | Éditeur → studio. |
| Importer la note | activé | Note Calibre 0-10 → note communautaire. |
| Retirer le HTML du résumé | activé | Nettoie le commentaire HTML. |

## Notes techniques

- **SQLite** : le plugin compile contre `Microsoft.Data.Sqlite` mais n'embarque pas la DLL
  (`<ExcludeAssets>runtime</ExcludeAssets>`) car le serveur Jellyfin la charge déjà. En cas
  d'erreur de chargement, retirer cette ligne du `.csproj` pour embarquer la bibliothèque.
- L'index de série Calibre peut être fractionnaire (ex. 1.5) ; Jellyfin n'accepte qu'un entier,
  la valeur est donc arrondie.
- La correspondance repose sur `books.path`. Si vos dossiers diffèrent de la structure standard
  `Auteur/Titre (id)` de Calibre, la correspondance peut échouer (voir les logs en niveau Debug).
