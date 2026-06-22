# Manuel d'installation et de configuration de ReVue VRO et ReVue Judge

Ce manuel couvre une configuration d'événement standard avec un ordinateur VRO et des ordinateurs séparés pour les juges et arbitres.

## Quelle application va où

Installez `ReVue VRO` uniquement sur l'ordinateur VRO.

L'ordinateur VRO enregistre la vidéo entrante, crée les fichiers de relecture, exécute l'interface locale ReVue VRO pour l'opérateur VRO et fournit les clips de relecture au panel de jugement.

Installez `ReVue Judge` sur chaque ordinateur de juge et d'arbitre.

ReVue Judge est le client de relecture du panel. Il n'enregistre pas la vidéo et ne remplace pas ReVue VRO sur l'ordinateur VRO.

## Avant l'installation

Assurez-vous que tous les ordinateurs sont sur le même réseau d'événement et que l'ordinateur VRO possède une adresse IP stable (non dynamique). Les ordinateurs des juges et des arbitres utiliseront cette adresse IP du VRO pour se connecter à ReVue VRO.

Sur les ordinateurs Windows 10, ReVue VRO et ReVue Judge nécessitent le runtime Microsoft Edge WebView2. Si l'une des applications s'ouvre sur une fenêtre vide ou échoue immédiatement sur un ordinateur Windows 10, installez le [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download-section) puis relancez l'application. N'installez pas WebView2 séparément sur Windows 11, car ce runtime est déjà inclus dans Windows 11.

## Configuration informatique recommandée

ReVue VRO effectue le travail le plus lourd. Il enregistre le flux entrant, crée la vidéo de relecture haute et basse résolution, sert les fichiers de relecture au panel et écrit/supprime une grande quantité de données vidéo pendant un événement.

Ordinateur VRO minimum recommandé pour ReVue VRO :

| Composant | Recommandation minimale | Configuration d'événement préférable |
| --- | --- | --- |
| CPU | Intel Core i5/i7 récent ou AMD Ryzen 5/7, 4 cœurs ou mieux | Intel Core i7/i9 ou AMD Ryzen 7/9, 6 cœurs ou mieux |
| RAM | `16 Go` | `32 Go` si disponible |
| Graphiques | Les graphiques intégrés peuvent suffire pour un usage basique | La prise en charge matérielle de l'encodage vidéo est fortement recommandée, comme Intel Quick Sync, NVIDIA NVENC ou l'encodage matériel AMD |
| Stockage | SSD, `500 Go` minimum | SSD, `1 To` ou plus |
| Réseau | Ethernet Gigabit filaire recommandé |  |

N'utilisez pas de disque dur mécanique pour l'ordinateur VRO. Évitez également les disques de petite capacité ou de marques douteuses. ReVue VRO écrit et supprime des données vidéo en continu, donc un SSD offre de meilleures performances d'enregistrement et de relecture. Un SSD de `500 Go` ou plus est recommandé, car les plus petits disques peuvent se remplir rapidement et s'user plus vite lorsqu'ils sont utilisés de manière répétée pour des charges de travail d'enregistrement vidéo. Assurez-vous qu'au moins 15 à 20 % de l'espace du disque reste disponible en permanence.

Pour les ordinateurs ReVue Judge, les exigences sont plus légères car ils reçoivent et lisent une vidéo de relecture basse résolution au lieu d'enregistrer et d'encoder le flux principal.

Ordinateur juge/arbitre minimum recommandé :

| Composant | Recommandation minimale |
| --- | --- |
| CPU | Intel Core i3/i5 récent ou AMD Ryzen 3/5 |
| RAM | `8 Go` |
| Graphiques | Des graphiques intégrés standards suffisent généralement |
| Stockage | SSD recommandé |
| Réseau | Wi-Fi fiable ou Ethernet |

Même si l'ordinateur VRO doit utiliser un Ethernet Gigabit filaire, les ordinateurs portables des juges et des arbitres peuvent généralement fonctionner correctement sur un bon réseau Wi-Fi local fermé pour l'événement, à condition que ce réseau sans fil ne soit pas partagé avec le public ou avec le trafic général du site.

Encodeur vidéo en direct recommandé :

- Utilisez un encodeur dédié qui fournit un flux RTSP à l'ordinateur VRO.
- Si la caméra vidéo prend en charge une sortie SDI, l'AVMatrix SE-1117 ou un encodeur SDI similaire est recommandé. Les encodeurs HDMI, comme le J-Tech ENCH-4 HDMI H.264 IPTV Encoder, conviennent également. Toutefois, les connexions SDI sont conçues pour les environnements vidéo professionnels, offrent un câblage plus fiable sur de longues distances et sont généralement moins sujettes aux déconnexions accidentelles ou aux problèmes de signal que le HDMI.
- Le flux de l'encodeur en direct préféré est `1080p 60 ips`. `1080p 30 ips` est acceptable si 60 ips n'est pas disponible. `1080i 60` est également acceptable lorsque la sortie progressive n'est pas disponible. En terminologie vidéo normale, `1080i 60` signifie 60 champs entrelacés par seconde, soit en pratique 30 images complètes par seconde.
- L'exigence importante est la cohérence : la fréquence d'images de la vidéo de démonstration/configurée doit correspondre à celle produite par le flux de l'encodeur.

## Windows Defender et SmartScreen

Windows peut avertir à propos des installateurs nouvellement téléchargés ou fournis, surtout avant que l'application n'ait acquis une réputation suffisante auprès de Microsoft SmartScreen.

Si Windows SmartScreen bloque l'installateur :

1. Cliquez sur `Plus d'informations`.
2. Vérifiez que l'éditeur/le fichier correspond bien à l'installateur ReVue VRO ou ReVue Judge attendu.
3. Cliquez sur `Exécuter quand même`.

Si Windows Defender ou le navigateur signale que le fichier est suspect :

1. Vérifiez que le fichier provient d'une source de confiance.
2. Si Windows affiche une option `Conserver` ou `Conserver quand même`, utilisez-la uniquement après avoir vérifié la source du fichier.
3. Si le fichier est bloqué après la copie, faites un clic droit sur l'installateur, choisissez `Propriétés`, cochez `Débloquer` si l'option est présente, puis cliquez sur `OK`.

Ne contournez pas ces avertissements pour des fichiers provenant d'une source inconnue.

## Avant l'installation

Assurez-vous que tous les ordinateurs sont sur le même réseau d'événement et que l'ordinateur VRO dispose d'une adresse IP stable (non dynamique).

Sur certains ordinateurs Windows 10, ReVue VRO et ReVue Judge nécessitent le runtime Microsoft Edge WebView2. Si l'une des applications s'ouvre sur une fenêtre vide ou échoue immédiatement sur un ordinateur Windows 10, installez le [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download-section) puis relancez l'application.

N'installez pas WebView2 séparément sur Windows 11, car ce runtime est déjà inclus dans Windows 11.

## Installer ReVue VRO sur l'ordinateur VRO

1. Exécutez l'installateur `ReVue-VRO-Setup-<version>.exe` sur l'ordinateur VRO.
2. Démarrez `ReVue VRO`.
3. Ouvrez l'écran des paramètres en cliquant sur l'icône d'engrenage située en haut à droite de la fenêtre.
4. Configurez la liaison CSS, la source vidéo, les paramètres d'encodage et de sauvegarde des vidéos.
5. Enregistrez les paramètres et redémarrez ReVue VRO.

## Installer ReVue Judge sur les ordinateurs de juge et d'arbitre

1. Exécutez l'installateur `ReVue-Judge-Setup-<version>.exe` sur chaque ordinateur de juge et d'arbitre.
2. Démarrez `ReVue Judge`.
3. Ouvrez l'écran des paramètres en cliquant sur l'icône d'engrenage située en haut à droite de la fenêtre.
4. Définissez `Server IP address` sur l'adresse IP de l'ordinateur VRO.
5. Définissez le rôle sur `Judge` ou `Referee`.
6. Enregistrez les paramètres.

`Server IP address` doit être l'adresse IP de l'ordinateur VRO, et non celle de l'ordinateur du juge ou de l'arbitre. Cette adresse DOIT être fixe/stable (non dynamique).

Exemple :

```text
192.168.6.60
```

## Configuration initiale de ReVue VRO

Ouvrez les paramètres de ReVue VRO sur l'ordinateur VRO en cliquant sur l'icône d'engrenage située en haut à droite de la fenêtre.

### Intégration CSS

Si vous intégrez ReVue VRO avec Legacy CSS, définissez `CSS Link Type` sur :

```text
Legacy CSS
```

Puis définissez `MSSQL Database Host` sur l'adresse IP de l'hôte de base de données CSS (généralement l'ordinateur EC).

Si vous intégrez ReVue VRO avec Online CSS ou Offline CSS, définissez `CSS Link Type` sur :

```text
None
```

L'affichage des codes d'élément exécutés et la détermination automatique des valeurs de mi-temps spécifiques à la discipline/catégorie/segment sont actuellement uniquement pris en charge pour Legacy CSS. Une prise en charge similaire pour Online CSS et Offline CSS est prévue.

Lorsque l'intégration CSS est définie sur `None`, ReVue VRO ne peut pas détecter automatiquement la catégorie, la discipline, le segment ou la mi-temps à partir des données CSS. Utilisez la liste déroulante manuelle `HW:` dans l'interface d'enregistrement/relecture pour sélectionner manuellement la valeur de mi-temps appropriée lorsque cela est nécessaire.

### Source vidéo

Activez le Demo Mode pour la formation ou la démonstration. Ce mode utilise une vidéo locale stockée sur l'ordinateur au lieu d'un flux vidéo RTSP comme source d'entrée. La vidéo locale peut être personnalisée en remplaçant le fichier `demovideo.mp4` situé dans `%LocalAppData%\ReVue\media\`.

Format de vidéo de démonstration pris en charge :

	Conteneur : MP4
	Codec vidéo : H.264 / AVC
	Fréquence d'images : constante, correspondant à celle produite par l'encodeur vidéo
	Résolution : 1920x1080 préférable, 1280x720 acceptable
	Audio : facultatif ; non nécessaire pour le Demo Mode
	Démarrage rapide : activé

À éviter : HEVC/H.265, HDR, fréquence d'images variable, codecs inhabituels.

Désactivez Demo Mode pour un enregistrement réel lors d'un événement ; définissez alors l'URL RTSP sur l'URL du flux de l'encodeur vidéo.

Exemple sans port spécial :

```text
rtsp://192.168.6.200/0
```

Exemple avec port et chemin explicites :

```text
rtsp://192.168.1.168:8554/video
```

Dans la plupart des cas, définissez `RTSP Transport Protocol` sur `UDP`. UDP est généralement le meilleur choix sur un réseau d'événement local propre, car il offre une latence plus faible.

Utilisez `TCP` uniquement si vous ne parvenez pas à établir une connexion RTSP fiable avec `UDP`.

### Paramètres d'encodage

Paramètres recommandés :

| Paramètre | Valeur recommandée | Remarques |
| --- | --- | --- |
| High-res Video GOP | `2` | Utilisé par ReVue VRO sur l'ordinateur VRO. Un GOP plus faible améliore la réactivité du déplacement dans la vidéo. |
| Low-res Video GOP | `30` | Utilisé par les clients ReVue Judge. |
| Low-res Video Bitrate | `3500` à `4000` kbps | Des valeurs plus élevées améliorent la qualité mais utilisent plus de bande passante réseau. |

Utilisez `3500` kbps ou moins lorsque la bande passante est limitée ou qu'un grand nombre de clients sont connectés. Utilisez `4000` kbps lorsque le réseau de l'événement est solide et qu'une meilleure qualité de relecture pour les juges est souhaitée.

Activez `Use Hardware Encoding` dans la plupart des situations. Désactivez-le uniquement si l'encodage matériel provoque un problème confirmé d'encodage vidéo. La désactivation de l'encodage matériel augmentera considérablement la charge CPU sur l'ordinateur VRO. La charge CPU doit être surveillée en conditions réelles d'événement après la configuration initiale.

## Exigences de pare-feu et de réseau

L'ordinateur VRO doit autoriser les ordinateurs des juges et des arbitres à se connecter à ReVue VRO.

Règle entrante requise sur l'ordinateur VRO :

```text
TCP 5050
```

ReVue VRO écoute sur :

```text
http://0.0.0.0:5050
```

ReVue Judge utilise le port TCP `5050` pour l'état, les données de relecture et le téléchargement des fichiers vidéo depuis l'ordinateur VRO.

Si Windows Firewall affiche une invite lors du premier lancement de ReVue VRO, autorisez l'accès sur le réseau privé/de l'événement.

Si les connexions échouent encore, les options comprennent :

- Ajouter une règle entrante Windows Firewall autorisant TCP `5050` pour `ReVue-VRO.exe`.
- Ajouter une règle entrante Windows Firewall autorisant TCP `5050` pour le profil réseau.
- Désactiver temporairement Windows Firewall sur l'ordinateur VRO pour le réseau de l'événement.

Désactiver Windows Firewall est simple pour le dépannage, mais une règle ciblée d'autorisation TCP `5050` est préférable lorsque c'est possible afin de maintenir la sécurité du réseau.

## Liste de vérification de connexion

Sur l'ordinateur VRO :

1. Démarrez ReVue VRO.
2. Vérifiez que l'ordinateur VRO est connecté au réseau de l'événement.
3. Vérifiez l'adresse IP du VRO.
4. Vérifiez que le port TCP `5050` est autorisé par le pare-feu.

Sur chaque ordinateur de juge/arbitre :

1. Démarrez ReVue Judge.
2. Ouvrez l'écran des paramètres en cliquant sur l'icône d'engrenage.
3. Saisissez l'adresse IP de l'ordinateur VRO.
4. Sélectionnez le rôle correct (`Judge` ou `Referee`).
5. Enregistrez les paramètres.
6. Vérifiez que ReVue Judge se connecte lorsque ReVue VRO est en cours d'exécution.

## Dépannage

Si ReVue Judge reste sur l'écran d'attente :

- Vérifiez que ReVue VRO est en cours d'exécution sur l'ordinateur VRO.
- Vérifiez que l'adresse IP du VRO dans ReVue Judge est correcte.
- Vérifiez que tous les ordinateurs sont sur le même LAN/VLAN.
- Vérifiez que le réseau de l'événement ne bloque pas le trafic client-à-client.
- Vérifiez que le port TCP `5050` est ouvert en entrée sur l'ordinateur VRO.

Si ReVue VRO n'affiche pas la vidéo :

- Vérifiez que Demo Mode est désactivé pour l'enregistrement d'un événement en direct.
- Vérifiez que l'URL RTSP est correcte.
- Vérifiez que l'encodeur est sous tension et connecté au réseau.
- Essayez d'abord `UDP`, puis `TCP` si le flux est instable ou indisponible.
- Vérifiez que l'adresse IP de l'encodeur est joignable depuis l'ordinateur VRO (par exemple, ouvrez une invite de commandes et pinguez l'adresse IP).

Si la vidéo de relecture des juges est de mauvaise qualité ou saccadée :

- Augmentez le débit du flux basse résolution au-dessus de `4000` kbps (ou éventuellement davantage) pour améliorer la qualité si la bande passante le permet.
- Réduisez le débit du flux basse résolution (par exemple `2500` kbps) si le réseau est saturé.
- Vérifiez que tous les clients disposent d'une connectivité réseau filaire ou sans fil solide.
- Conservez le GOP basse résolution à `30` sauf indication contraire.
