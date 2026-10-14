<p align="center"><a href="README.md">English</a> · <b>Français</b> · <a href="README.es.md">Español</a></p>

# Aide d'Echo

Echo enregistre vos réunions Teams (ou en présentiel) sous Windows, transcrit qui a dit quoi et
rédige un compte rendu prêt à envoyer. Aucun robot ne rejoint la réunion : tout se passe sur votre
PC.

> **Windows et Microsoft Teams uniquement pour l'instant.** L'interface existe en français et en
> anglais.

**Sommaire**

- [Prise en main](#getting-started)
- [Enregistrer et le widget](#recording)
- [Après la réunion](#after-the-meeting)
- [Obtenir une clé API](#api-keys) : [Mistral](#mistral) · [Cloudflare](#cloudflare) · [Google Gemini](#gemini) · [OpenRouter](#openrouter)
- [Dépannage](#troubleshooting)
- [Tous les réglages en un coup d'œil](#settings)

<a id="getting-started"></a>

## Prise en main

<a id="install"></a>

### Installer Echo

1. Téléchargez **[Echo-Setup.exe](https://github.com/romainio/echo-recorder/releases/latest/download/Echo-Setup.exe)**,
   puis double-cliquez dessus.
2. Si Windows affiche « Windows a protégé votre ordinateur », cliquez sur **« Informations
   complémentaires »**, puis sur **« Exécuter quand même »**. Ce message apparaît pour toute
   application qui n'a pas encore de signature numérique.
3. Cliquez sur **« Installer »**. Echo s'installe dans votre profil (aucun droit administrateur),
   ajoute un raccourci au menu Démarrer et se lance.

Ensuite, Echo se met à jour tout seul. Pour réinstaller, relancez simplement `Echo-Setup.exe` :
vos réglages, vos clés et vos réunions sont conservés. Pour désinstaller : **Paramètres Windows >
Applications > Applications installées > Echo**.

### Trois étapes

1. **Ajoutez une clé API.** C'est à faire une seule fois. Echo a besoin de la clé d'un fournisseur
   d'IA pour transcrire et rédiger les comptes rendus. Choisissez-en un dans
   [Obtenir une clé API](#api-keys), collez la clé dans **Paramètres > Fournisseurs**, puis cliquez
   sur **« Tester »**.
2. **Lancez un enregistrement.** Cliquez sur **« Nouvel enregistrement »**, en haut de la barre
   latérale. Echo capte le son de la réunion Teams tout seul, ou votre micro en présentiel.
3. **Arrêtez, récupérez le CR.** En fin de réunion, cliquez sur **« Arrêter & générer le CR »**.
   Echo transcrit, puis rédige le compte rendu. Vous le trouvez dans la fiche de la réunion.

### La fenêtre principale

**La barre de titre**

- **Recherche** (`Ctrl K`) : tapez un nom, la liste des réunions se filtre pendant que vous écrivez.
- **Soleil / lune** : bascule entre thème clair et sombre. Le réglage complet, avec « Système », est
  dans Paramètres > Général.
- **La croix** ne ferme pas Echo. L'application reste active dans la **zone de notification**, près
  de l'horloge, et continue de surveiller vos réunions. Cliquez sur son icône pour rouvrir la
  fenêtre.

**La barre latérale**

- **Nouvel enregistrement** démarre une réunion. Pendant l'enregistrement, ce même bouton l'arrête.
- **Importer** reprend un transcript Teams (.docx ou .vtt) **ou un fichier audio** (.mp3, .m4a,
  .wav, .mp4 et d'autres). Echo crée la fiche, puis le CR et un résumé.
- **Dossier** crée un dossier de mission (un nom, une couleur) pour classer vos réunions.
- **Accueil** et **Toutes les réunions** ouvrent la vue de synthèse et la liste complète. Le
  compteur affiche le total.
- **Dossiers** liste vos missions. Un clic filtre la liste sur ce dossier, le « + » en crée un.
- **Ouvrir le dossier local** ouvre le dossier Windows qui contient tous vos enregistrements.
- **Votre nom**, tout en bas, ouvre les **Paramètres**.

**L'accueil**

- **Trois compteurs** : réunions, comptes rendus produits, temps total enregistré.
- **La bannière d'enregistrement** s'affiche pendant une réunion : minuteur, nom de la réunion,
  bouton **Widget** (ramène le widget flottant s'il a été masqué) et **« Arrêter & générer le CR »**.
  Les alertes (micro, son) s'affichent à droite.
- **Réunions récentes** et **Dossiers** donnent un accès direct aux fiches et aux missions.

**Toutes les réunions**

- **Les filtres** : par nom, dossier de mission, statut et période (7, 30 ou 90 jours).
- **Sélectionner** active la sélection multiple : cochez des réunions, puis **Générer le
  transcript** (relance en lot) ou **Supprimer**. « Tout sélectionner » agit sur la liste filtrée.
- **La corbeille** d'une carte supprime cette réunion et tout son dossier.

| Statut | Ce qu'il signifie |
|---|---|
| **Compte rendu** | Transcript et CR générés. La fiche est complète. |
| **Enregistré** | Audio capté, mais pas encore de transcript ou de CR. |
| **Traitement** | Transcription ou rédaction en cours. |
| **En attente de l'API** | Le fournisseur d'IA était saturé (429) ou indisponible. Echo réessaie tout seul dans 10 min, puis 30, puis 60. Rien à faire, l'audio est conservé. |
| **Erreur** | Une étape a échoué pour une cause qu'attendre ne règle pas (clé refusée, modèle hors de votre offre). La notification précise la cause. Relancez depuis la fiche avec « Générer un compte rendu » ou « Relancer la transcription ». |

<a id="recording"></a>

## Enregistrer et le widget

Pendant l'enregistrement, une bannière s'affiche sur l'accueil et un petit **widget flottant** reste
posé par-dessus vos fenêtres. Vous gardez tout à portée de main sans quitter la réunion.

### Le widget flottant

Une petite carte avec le minuteur, les notes, les moments clés, les captures et le bouton d'arrêt.
Son en-tête affiche le **nom de la réunion** et son état (« Enregistrement en cours », ou une alerte
en cas de souci). Attrapez l'en-tête pour le **déplacer**, et le coin en bas à droite pour **changer
sa largeur**. La hauteur suit le contenu.

**Les boutons de l'en-tête**

| Bouton | Ce qu'il fait |
|---|---|
| **Épingle** | Garde le widget au premier plan (par défaut). Recliquez pour le laisser passer derrière les autres fenêtres. |
| **Enrouler en rail** | Replie le widget en une fine barre verticale. Pratique quand vous partagez votre écran. |
| **Masquer** | Cache le widget. L'enregistrement continue. Le bouton « Widget » de la bannière d'accueil le fait revenir. |

**Notes et dernière capture**

- **Prendre une note** : cliquez sur « Cliquez pour saisir une note » (ou sur le bouton note, en
  bas), tapez, puis `Entrée`. Chaque note part dans `notes.md` et apparaît dans l'onglet « Notes &
  moments clés » de la réunion.
- **Horodatage** (bouton horloge) : actif par défaut, il ajoute à chaque note le minutage de la
  réunion (« 12:40 »). Cliquez dessus pour écrire une note sans horodatage.
- **Lier à la capture** (bouton maillon) : rattache la note à la dernière capture (préfixe
  « [capture] »). Utile pour commenter une slide précise.
- **La vignette « Dernière capture »** apparaît après une capture, avec l'heure et le compteur. Un
  clic ouvre l'image.

**Les boutons du bas**

| Bouton | Ce qu'il fait |
|---|---|
| **Notes** | Ouvre ou ferme le panneau de notes. |
| **Moment clé ⚑** | Marque l'instant d'une note « Moment clé » horodatée, pour retrouver le passage plus tard. |
| **Capture d'écran** | Photographie l'écran (les slides partagées). L'image va dans l'onglet **Captures** de la réunion et s'affiche dans le widget. |
| **Stop** | Arrête l'enregistrement et lance le traitement (transcription, puis CR et résumé si les automatismes sont actifs). |

**Le mode rail (widget enroulé)**

Enroulé, le widget devient une barre verticale d'environ 50 px : un point rouge qui pulse
(l'enregistrement tourne), une poignée pour le déplacer, et quatre boutons : **Dérouler** (retour à
la carte complète), **Capture**, **Moment clé ⚑** et **Stop**, comme sur la carte.

### Détection et automatismes

Tout se règle dans **Paramètres > Enregistrement**.

- **Détection des réunions** : Echo surveille Teams et vous **propose** d'enregistrer dès que vous
  rejoignez un appel.
- **Enregistrement automatique** : plus de question, chaque réunion Teams détectée est enregistrée
  (cela active aussi la détection). Quand vous quittez la réunion, Echo s'arrête tout seul après un
  court délai, puis enchaîne transcription, CR et résumé selon vos réglages.
- **Capture d'écran automatique** : les slides partagées sont photographiées régulièrement, en plus
  de vos captures.
- **Raccourci clavier global** : démarre ou arrête l'enregistrement depuis n'importe quelle
  application (`Ctrl+Alt+R` par défaut).

En **présentiel**, quand aucune réunion Teams n'est détectée, Echo enregistre le micro seul. Posez le
PC au centre de la table.

> Le son des participants doit sortir sur **ce PC** (haut-parleurs ou casque). S'il sort ailleurs
> (un autre poste, une TV), Echo enregistre le micro seul et vous prévient.

<a id="after-the-meeting"></a>

## Après la réunion

Cliquez sur une réunion pour ouvrir sa **fiche**. Elle a des onglets : Résumé, Transcription,
Compte rendu, Captures, Participants et Notes.

### L'en-tête de la fiche

- **Le crayon**, à côté du titre, renomme la réunion (la fiche et les fichiers).
- **L'icône dossier** ouvre le dossier Windows de la réunion.
- **Le menu « Sans dossier »** range la réunion dans un dossier de mission.
- **Générer un compte rendu** ouvre l'onglet CR et lance la rédaction avec le modèle choisi.
- **Relancer la transcription** refait tout depuis l'audio. Utile après un ajout au vocabulaire, ou
  si une transcription a échoué. L'audio n'est jamais réenregistré, seulement réanalysé.
- **Partager & exporter** ouvre le menu des fichiers (voir plus bas).

### Onglet Résumé

L'essentiel de la réunion en 5 à 10 puces courtes, fidèles au transcript, sans rien inventer. Il se
génère à la demande (« Générer le résumé ») ou automatiquement. **« Régénérer »** le refait, par
exemple une fois les locuteurs identifiés.

### Onglet Transcription

Le verbatim est découpé par prise de parole. Chaque tour a son heure et son intervenant, lu depuis la
fenêtre Teams (là encore, aucun robot dans la réunion).

- **Recherche** : filtre les prises de parole en direct, noms compris.
- **Copier** : copie tout le verbatim horodaté.
- **Identifier un locuteur** : les voix non reconnues apparaissent en « Locuteur N » avec un crayon.
  Donnez le vrai nom une fois, il s'applique à **toutes** les prises de parole de cette voix. Vous
  pouvez ensuite régénérer le CR avec les bons noms.
- **▶ Écouter le passage** : au survol d'une prise de parole, un bouton lecture joue l'extrait, avec
  quelques secondes avant et après.
- **Lecteur de piste complète** en haut de l'onglet. Cliquez l'heure d'une prise de parole pour vous
  y placer.

### Le vocabulaire métier

Dans **Paramètres > Vocabulaire**, listez les termes que la transcription écorche : noms de client,
projets, sigles, outils. Ils guident la reconnaissance vocale et sont corrigés dans le transcript et
dans le CR. Une entrée par ligne : le **terme** seul, ou **variante1|variante2=>Terme** pour
remplacer des déformations connues par la bonne orthographe.

Le bouton **« Prompt à copier-coller »** copie un prompt prêt à l'emploi. Collez-le dans votre
assistant IA avec vos documents de mission (notes, mails, glossaire) : il vous rend la liste au bon
format. Un vocabulaire peut être **général** ou dédié à un **dossier de mission**
(« S'applique à »).

### Onglet Compte rendu et les modèles

Choisissez un **modèle** et la **langue de sortie** (celle de la réunion, ou français, anglais,
espagnol, allemand), puis cliquez sur **Générer**. Le CR s'affiche dans l'onglet et les fichiers
listés sous « Fichiers générés » sont prêts à envoyer. Régénérer remplace le CR précédent.

- **Deux modèles de base** : **Détaillé** (structuré, avec décisions, actions et points par sujet)
  et **Synthèse** (juste l'essentiel).
- **Vos modèles** : dans **Paramètres > Comptes rendus**, partez d'une base, ajoutez des
  **consignes** (« mets les actions en tableau », « commence par les risques ») et un **exemple à
  imiter** (collez un CR dont vous aimez le style). Le modèle par défaut est celui des automatismes.
- **Formats** : .eml (un brouillon Outlook prêt à envoyer), .md et .docx, au choix dans les
  Paramètres.

### Onglet Captures

Toutes les captures de la réunion, manuelles ou automatiques, en grille. Un clic ouvre l'image. Le
crayon ajoute une note liée à cette capture.

### Onglet Participants et e-mails

Teams ne donne que les **noms**. Saisissez l'e-mail d'une personne **une seule fois** : Echo le
retient pour toutes les réunions où elle apparaît. Ces e-mails pré-remplissent le brouillon .eml du
CR.

### Onglet Notes & moments clés

Tout ce qui a été saisi pendant la réunion : notes horodatées, **moments clés ⚑** et notes liées aux
captures. **Modifier** ouvre l'édition libre en Markdown. Corrigez, complétez, puis **Enregistrez** :
le fichier `notes.md` de la réunion est mis à jour.

### Partager & exporter

- **Dossier de la réunion** : tous les fichiers, dans l'Explorateur.
- **Comptes rendus** : chaque fichier généré (.eml, .md, .docx) s'ouvre directement. Le .eml s'ouvre
  dans Outlook, prêt à envoyer.
- **Audio** : le MP3 d'écoute de la réunion.
- **Dossier Claude (.md)** : un dossier prêt pour l'analyse dans un assistant IA, avec le contexte de
  la réunion, le transcript corrigé et les notes en Markdown.

### Les dossiers de mission

Créez un dossier (bouton « Dossier » ou « + » de la barre latérale), puis rangez chaque réunion
depuis sa fiche avec le menu « Sans dossier ». Le dossier devient un **filtre** partout et peut avoir
son **propre vocabulaire** (Paramètres > Vocabulaire > « S'applique à »), ajouté au vocabulaire
général.

### Importer un transcript Teams ou un fichier audio

Le bouton **Importer** (barre latérale, ou Paramètres > Fournisseurs > Microsoft Teams) accepte :

- un transcript exporté de Teams (.docx ou .vtt) : Echo crée la fiche avec les participants et le
  verbatim, puis peut rédiger le CR et un résumé ;
- un **fichier audio ou vidéo** : enregistrement de téléphone ou de dictaphone, note vocale,
  enregistrement Teams (.mp4). Formats : .mp3, .m4a, .wav, .aac, .wma, .ogg, .opus, .flac, .amr,
  .3gp, .mp4, .mov, .mkv, .webm, .avi. Echo crée la réunion (nom = nom du fichier, heure = début
  estimé), la transcrit avec votre fournisseur, puis rédige le CR et le résumé. Le fichier d'origine
  n'est jamais modifié. Avec une seule piste et sans participants Teams, les voix sont numérotées
  (« Participant 0 », « Participant 1 »...) quand le moteur sait les distinguer (Voxtral, Gemini).
  Renommez-les depuis la fiche.

<a id="api-keys"></a>

## Obtenir une clé API

Echo envoie l'audio à transcrire et le texte à résumer au fournisseur d'IA que vous choisissez, avec
**votre propre clé**. La clé est stockée chiffrée sur votre PC (DPAPI de Windows).

| Fournisseur | Transcription | CR, résumé, correction | Offre gratuite |
|---|---|---|---|
| [Mistral](#mistral) | Oui (Voxtral) | Oui | Oui, avec limites |
| [Cloudflare Workers AI](#cloudflare) | Oui (Whisper) | Oui | Oui, quota par jour |
| [Google Gemini](#gemini) | Oui | Oui | Oui, avec limites |
| [OpenRouter](#openrouter) | Non | Oui | Oui, modèles `:free` |

**Comment ça marche dans Echo**

- Tout se passe dans **Paramètres > Fournisseurs**. Choisissez le **moteur de transcription** et le
  **moteur de compte rendu et résumé**. Ce peuvent être deux fournisseurs différents.
- Collez chaque clé, cliquez sur **« Enregistrer »**, puis sur **« Tester »**. La ligne sous le
  bouton indique si la clé fonctionne.
- Les modèles marqués **Free** passent par l'offre gratuite du fournisseur. Activez **« Afficher le
  détail des modèles »** pour voir et changer le modèle de chaque tâche.
- **Bascule automatique** (active par défaut) : si un fournisseur est saturé ou refuse le modèle,
  Echo passe sur l'autre fournisseur dont la clé est renseignée.
- Conseil : configurez deux fournisseurs (par exemple Mistral et Cloudflare). Si l'un échoue, l'autre
  prend le relais.

Les prix, les limites et les écrans de ces services changent souvent. Les étapes ci-dessous ont été
vérifiées le 13 octobre 2026. Les sites des fournisseurs sont en anglais : les libellés des boutons
sont donnés tels qu'ils s'affichent.

<a id="mistral"></a>

### Mistral

Transcription avec Voxtral, plus CR, résumé et correction.

**Créer la clé**

1. Allez sur [console.mistral.ai](https://console.mistral.ai) et inscrivez-vous ou connectez-vous.
2. Si on vous le demande, choisissez une offre. L'offre gratuite s'appelle **« Free mode »**
   (anciennement « Experiment »).
3. Ouvrez **« API Keys »** dans le menu.
4. Cliquez sur **« Create new key »**. Donnez-lui un nom (par exemple `Echo`), gardez l'espace de
   travail et l'expiration par défaut, puis cliquez à nouveau sur **« Create new key »**.
5. Copiez la clé tout de suite. **Elle n'est affichée qu'une fois.** Si vous la perdez, créez-en une
   autre.
6. Dans Echo : **Paramètres > Fournisseurs > Mistral**, collez la clé dans **« Clé API Mistral »**,
   cliquez sur **« Enregistrer »**, puis sur **« Tester »**.

Utilisez une clé de la console API. Une clé créée pour d'autres produits Mistral (par exemple
« Vibe » ou « Mistral Code ») ne fonctionne pas ici.

**Offre gratuite et limites**

- La documentation de Mistral n'est pas cohérente : une page dit qu'aucune carte bancaire n'est
  nécessaire, une autre que les clés restent inactives tant qu'aucun moyen de paiement n'est ajouté.
  Selon votre compte, une carte ou un numéro de téléphone peut vous être demandé.
- Vos limites sont dans la console : **Admin Panel > API > Limits**.
- Voxtral en Free mode n'est pas documenté. Lors de notre test du 13 octobre 2026, `voxtral-mini` a
  fonctionné sur une clé gratuite (60 requêtes par minute), tout comme `ministral-8b`, `ministral-3b`
  et `open-mistral-nemo`.
- Certains modèles (par exemple `mistral-small` ou `mistral-medium`) ont répondu 429 avec une limite
  de **0 requête par minute** : ils ne font pas partie de votre offre. Echo passe alors sur un modèle
  marqué **Free**.

**Confidentialité**

- Avec l'offre gratuite, Mistral peut utiliser vos données pour améliorer ses modèles. Vous pouvez
  le refuser dans **Admin > Privacy > « Anonymous improvement data »**.
- Avec une offre payante, vos données ne servent pas à l'entraînement.

**Erreurs fréquentes**

| Erreur | Signification | Quoi faire |
|---|---|---|
| 401 | Clé fausse, révoquée ou expirée | Créez une nouvelle clé et collez-la à nouveau. |
| 402 | Paiement nécessaire | Ajoutez un moyen de paiement dans la console, ou utilisez un autre fournisseur. |
| 429 | Trop de requêtes, ou quota atteint | Patientez. Echo réessaie, puis bascule sur l'autre fournisseur. |
| 429 avec « 0 requête par minute » | Modèle hors de votre offre | Choisissez un modèle marqué **Free**. Echo le fait tout seul. |

<a id="cloudflare"></a>

### Cloudflare Workers AI

Transcription avec Whisper (`@cf/openai/whisper-large-v3-turbo`), plus CR, résumé et correction.
Cloudflare demande **deux valeurs** : un **jeton API** et votre **Account ID** (identifiant de
compte).

**Créer le jeton**

1. Allez sur [dash.cloudflare.com](https://dash.cloudflare.com) et inscrivez-vous ou connectez-vous.
2. Ouvrez **Workers AI** (rubrique « AI » du menu).
3. Cliquez sur **« Use REST API »**.
4. Cliquez sur **« Create a Workers AI API Token »**, vérifiez les permissions, puis cliquez sur
   **« Create API Token »**.
5. Cliquez sur **« Copy API Token »**. **Il n'est affiché qu'une fois.** Les jetons commencent par
   `cfut_`.
6. Sur la même page, copiez votre **Account ID** (« Get Account ID »). Il fait 32 caractères.
7. Dans Echo : **Paramètres > Fournisseurs > Cloudflare Workers AI**, collez l'Account ID dans
   **« Identifiant de compte Cloudflare »**, le jeton dans **« Jeton API Cloudflare »**, cliquez sur
   **« Enregistrer »**, puis sur **« Tester »**.

Autre chemin : **My Profile > API Tokens > Create Token**, puis un jeton personnalisé avec les
permissions **Workers AI : Read** et **Workers AI : Edit**.

**Offre gratuite et limites**

- L'offre gratuite donne **10 000 neurons par jour**, remis à zéro à 00:00 UTC. Pas de dépassement
  possible en gratuit : une fois le quota consommé, les requêtes sont refusées jusqu'au lendemain.
- Whisper turbo coûte environ 46,63 neurons par minute d'audio. D'après notre calcul, cela fait
  environ **214 minutes, soit à peu près 3 h 30 d'audio par jour**. C'est une estimation : le CR et
  le résumé puisent dans le même quota.
- Whisper ne distingue pas les voix. Echo attribue les tours d'après la piste (vous ou les autres
  participants) et les noms Teams relevés pendant la réunion.

**Confidentialité**

Cloudflare indique ne pas utiliser vos données pour entraîner des modèles, en gratuit comme en
payant.

**Erreurs fréquentes**

| Code | Signification | Quoi faire |
|---|---|---|
| 3036 | Quota du jour épuisé | Attendez 00:00 UTC, ou utilisez un autre fournisseur. |
| 3040 | Plus de capacité pour l'instant | Réessayez plus tard. Echo réessaie tout seul. |
| 5035 | Modèle réservé aux offres payantes | Choisissez un modèle marqué **Free**. |
| 3006 | Requête trop grosse | Echo envoie l'audio par morceaux de 10 minutes. Si cela se produit encore, relancez la transcription. |
| 3042 | Modèle invalide | Vérifiez le nom du modèle, ou choisissez-en un dans la liste. |
| « Failed to decode audio file » | Le modèle de transcription n'accepte pas le format audio d'Echo | Prenez `@cf/openai/whisper-large-v3-turbo`, puis « Relancer la transcription ». |

<a id="gemini"></a>

### Google Gemini

Transcription, CR, résumé et correction avec une clé Google AI Studio.

**Créer la clé**

1. Allez sur [aistudio.google.com/apikey](https://aistudio.google.com/apikey) et connectez-vous avec
   un compte Google.
2. Cliquez sur **« Get API key »** si besoin, puis sur **« Create API key »**.
3. Pour un nouveau compte, un projet et une clé sont créés pour vous. Sinon, choisissez un projet,
   ou importez-en un avec **« Import projects »**.
4. Copiez la clé. Bonne pratique : limitez-la à la **Gemini API**.
5. Dans Echo : **Paramètres > Fournisseurs > Google Gemini**, collez la clé dans **« Clé API Google
   (Gemini) »**, cliquez sur **« Enregistrer »**, puis sur **« Tester »**.

Avec un compte Google professionnel (Google Workspace), le bouton peut être grisé si
l'administrateur ne vous a pas donné les droits. Demandez-les-lui, ou utilisez un compte personnel.

**Offre gratuite et limites**

- Gratuit sans facturation. Les modèles flash portent l'étiquette **Free** dans Echo.
- Les limites s'appliquent **par projet**, pas par clé. Consultez-les sur
  [aistudio.google.com/rate-limit](https://aistudio.google.com/rate-limit).
- Les quotas du jour repartent à minuit, heure du Pacifique.

**Confidentialité**

- Google indique que dans l'UE et l'EEE, les règles de confidentialité du service payant (pas
  d'entraînement sur vos données) s'appliquent aussi à l'offre gratuite. Une clause des conditions
  (« only Paid Services for API clients in the EEA ») reste ambiguë. En cas de doute, activez la
  facturation ou utilisez un autre fournisseur.
- Hors de ces régions, les données de l'offre gratuite peuvent servir à améliorer les produits
  Google.

**Erreurs fréquentes**

| Erreur | Signification | Quoi faire |
|---|---|---|
| 400 / 403 | Clé invalide ou non autorisée pour la Gemini API | Vérifiez les restrictions de la clé, ou créez-en une nouvelle. |
| 429 | Quota du projet atteint | Attendez la remise à zéro, ou changez de moteur. Echo bascule tout seul. |

<a id="openrouter"></a>

### OpenRouter

CR, résumé et correction uniquement. **OpenRouter ne transcrit pas** : associez-le à Mistral,
Cloudflare ou Gemini pour la transcription.

**Créer la clé**

1. Allez sur [openrouter.ai/keys](https://openrouter.ai/keys) et inscrivez-vous ou connectez-vous.
2. Cliquez sur le bouton de création de clé (« Create API Key » ou « Create Key » selon la version
   de la page).
3. Donnez-lui un nom et choisissez une limite de crédit (ou aucune). Une limite est conseillée.
4. Copiez la clé. **Elle n'est affichée qu'une fois.**
5. Dans Echo : **Paramètres > Fournisseurs > OpenRouter**, collez la clé dans **« Clé API
   OpenRouter »**, cliquez sur **« Enregistrer »**, puis sur **« Tester »**.

**Offre gratuite et limites**

- Les modèles dont le nom finit par `:free` ne coûtent rien. Echo les affiche avec l'étiquette
  **Free**.
- Modèles gratuits : **20 requêtes par minute**, et **50 requêtes par jour** si vous avez acheté
  moins de 10 USD de crédits, ou **1 000 par jour** à partir de 10 USD de crédits.

**Confidentialité**

Certains modèles gratuits tournent chez des hébergeurs qui peuvent conserver ou utiliser vos
requêtes. Vous choisissez ce que vous acceptez dans
[openrouter.ai/settings/privacy](https://openrouter.ai/settings/privacy).

**Erreurs fréquentes**

| Erreur | Signification | Quoi faire |
|---|---|---|
| 503 ou « No endpoints found matching your data policy » | Vos réglages de confidentialité bloquent tous les hébergeurs de ce modèle gratuit | Dans openrouter.ai/settings/privacy, autorisez les hébergeurs gratuits qui peuvent entraîner sur les données, ou choisissez un autre modèle. |
| 402 | Solde négatif (même pour les modèles `:free`) | Ajoutez du crédit, ou utilisez un autre fournisseur. |
| 429 | Quota par minute ou par jour atteint | Patientez. Echo réessaie, puis bascule sur l'autre fournisseur. |

<a id="troubleshooting"></a>

## Dépannage

### Les problèmes courants

- **La transcription échoue.** Vérifiez la clé (Paramètres > Fournisseurs > **« Tester »**) et votre
  quota chez le fournisseur. Le détail est dans `echo.log`, dans `%LOCALAPPDATA%\Echo`.
- **Saturé (429).** La clé a atteint sa limite ou le service est chargé. Echo réessaie pendant
  environ deux minutes, bascule sur l'autre fournisseur si sa clé est renseignée, puis met la
  réunion « En attente de l'API » et réessaie dans 10, 30 et 60 minutes. Un transcript déjà produit
  n'est jamais perdu.
- **« Modèle indisponible sur le palier de cette clé » (403).** Votre clé n'a pas accès à ce modèle.
  Dans Paramètres > Fournisseurs, activez « Afficher le détail des modèles » et choisissez un modèle
  marqué **Free**, ou passez sur un autre fournisseur.
- **Le bouton rouge « Clé API absente · Aide »** dans la barre latérale. Une clé nécessaire aux
  moteurs choisis manque, ou un appel a échoué. Cliquez dessus pour ouvrir cette aide.
- **Quel moteur est vraiment utilisé ?** Le choix se fait à un seul endroit : Paramètres >
  Fournisseurs. Les pages Transcription et Comptes rendus affichent le moteur et le modèle en
  vigueur.
- **Place sur le disque.** Paramètres > Stockage indique ce que pèsent vos enregistrements. Une
  heure de réunion pèse environ 1 Go pendant l'enregistrement, puis l'audio est archivé en MP3.
  **« Nettoyer »** applique tout de suite les règles de conservation. Transcripts, CR, notes et MP3
  d'écoute ne sont jamais supprimés.
- **« Un enregistrement est déjà en cours ».** Un seul enregistrement à la fois. Si le widget a
  disparu, utilisez le raccourci clavier ou l'icône près de l'horloge pour arrêter.
- **« Enregistrement(s) interrompu(s) récupéré(s) ».** Au démarrage, Echo répare les réunions mal
  arrêtées (PC éteint, application fermée). Elles apparaissent dans la liste, prêtes à transcrire.
- **Pas de noms de locuteurs.** Echo pose le réglage Teams nécessaire au premier lancement. Il suffit
  ensuite de **quitter complètement Teams une fois** (clic droit sur l'icône Teams > Quitter) puis
  de le rouvrir.
- **Participants non détectés (affichage compact).** Quand la réunion est réduite en **petite
  vignette flottante**, Teams n'expose ni les participants ni qui parle. Gardez la **fenêtre de
  réunion agrandie**, au moins de temps en temps. Echo vous prévient et rattrape les noms dès que
  vous l'agrandissez.
- **« Son des participants indisponible ».** Le son de la réunion doit sortir sur **ce PC**.
  L'enregistrement continue au micro seul.
- **Votre voix manque.** Vérifiez que le bon **micro** était le périphérique par défaut de Windows,
  et que Teams ne l'utilisait pas en exclusif. L'audio est conservé : « Relancer la transcription »
  réessaie.
- **Le widget a disparu.** Il a été masqué ou enroulé. Le bouton **Widget** de la bannière d'accueil
  le ramène pendant un enregistrement.
- **La fenêtre a disparu.** Echo tourne toujours dans la **zone de notification** (la flèche
  « afficher les icônes cachées »). Cliquez sur son icône.
- **Mises à jour.** Au démarrage, Echo cherche une nouvelle version sur GitHub et vous la propose
  (Paramètres > Général > « Vérifier au démarrage »). **« Vérifier maintenant »** vérifie tout de
  suite. Si quelque chose coince après une mise à jour, **« Version précédente »** restaure
  l'ancienne installation.

### Les fichiers produits

Chaque réunion a son dossier dans `%LOCALAPPDATA%\Echo\recordings` (bouton « Ouvrir le dossier
local »).

| Fichier | Contenu |
|---|---|
| **&lt;Titre&gt; - Compte rendu.eml / .md / .docx** | Le compte rendu, prêt à envoyer depuis Outlook (« Minutes » si l'application est en anglais) |
| **&lt;Titre&gt; - Transcript.txt** | La transcription par locuteur, corrigée |
| **&lt;Titre&gt; - Résumé.md** | Le résumé court (« Summary » en anglais) |
| **&lt;Titre&gt; - Audio.mp3** | L'enregistrement audio |
| **notes.md** | Vos notes et moments clés horodatés |
| **Captures/** | Les captures d'écran |
| **.echo/** (caché) | Les fichiers de travail d'Echo : pistes brutes archivées (mic.mp3 / system.mp3, pour retranscrire), transcription brute, infos de la réunion, journaux |

&lt;Titre&gt; est le nom de la réunion : renommer la réunion renomme ces fichiers. Les réunions plus
anciennes sont rangées ainsi au premier démarrage d'Echo.

Après transcription, l'audio brut (WAV, environ 1 Go par heure) est archivé en un MP3 par piste,
environ 20 fois plus léger. La retranscription reste possible. « Conserver les WAV originaux »
désactive cet archivage, et « Suppression de l'audio (jours) » supprime l'audio des réunions
transcrites après N jours.

<a id="settings"></a>

## Tous les réglages en un coup d'œil

| Paramètres > | Ce que vous y trouvez |
|---|---|
| **Compte** | Votre nom, qui signe les comptes rendus. |
| **Enregistrement** | Détection des réunions, enregistrement automatique, capture d'écran automatique, zone des captures (fenêtre Teams ou tout l'écran), raccourci clavier global (`Ctrl+Alt+R` par défaut, modifiable et désactivable). |
| **Transcription** | Le moteur en vigueur (choisi dans Fournisseurs), la langue de la réunion (Auto, français ou anglais), la transcription automatique en fin de réunion, la correction du vocabulaire (LLM). |
| **Vocabulaire** | Vos termes métier et corrections, généraux ou par mission, l'import de fichier, le bouton « Prompt à copier-coller ». |
| **Comptes rendus** | Le moteur en vigueur, le CR automatique, la langue du CR, les formats (.eml, .md, .docx), le modèle par défaut, vos modèles. |
| **Fournisseurs** | Moteur de transcription, moteur de CR et résumé, bascule automatique, une clé par fournisseur avec « Enregistrer » et « Tester », l'identifiant de compte Cloudflare, « Afficher le détail des modèles » (le modèle de chaque tâche, avec l'étiquette **Free**), l'import de transcript Teams. |
| **Stockage** | Place occupée, suppression de l'audio après N jours, réunions en échec (jours), conservation des WAV originaux, « Nettoyer maintenant ». |
| **Général** | Thème, langue de l'application (français ou anglais), lancement au démarrage de Windows, dossier des enregistrements, mises à jour (« Vérifier au démarrage », « Vérifier maintenant », « Version précédente »), aide et version. |
