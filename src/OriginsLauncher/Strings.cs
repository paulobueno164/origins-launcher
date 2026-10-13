using System.Globalization;
using System.IO;
using System.Runtime.Serialization;

namespace OriginsLauncher
{
    /// <summary>Launcher texts in the three game languages; <see cref="L.S" /> is the current set.</summary>
    internal sealed class Strings
    {
        public string Code, Name;
        public string Play, Site, Check, Language, Starting;
        public string NoWrite, NoWriteDetail, LookingForUpdates, ServerDown, ServerDownPlay, CannotConnect, CannotConnectDetail;
        public string Ready, FilesUpdated, UpToDate, GameOpen, UpdateError, ServerSlow, TryAgain, CannotStart;
        public string Subtitle, News, Welcome, ClientVersion, DateFormat;
        public string VerifyingFiles, VerifyingDetail, Downloading, Retry, UpdatingLauncher, BadLauncher, BadManifest;
        public string DownloadingPack, Unpacking;
        public string UninstallTitle, UninstallConfirm, UninstallGameOpen, UninstallDone;
        public string Corrupted, ConnInterrupted, ConnStalled, CannotReplace, Of, Remaining;
        public string InstallTitle, InstallHeading, InstallInfo, Browse, Install, Cancel, PickFolder, InstallFailed;

        public static readonly Strings En = new Strings
        {
            Code = "en", Name = "English",
            Play = "PLAY", Site = "Website", Check = "Verify files", Language = "Language", Starting = "Starting...",
            NoWrite = "No permission to write to this folder.",
            NoWriteDetail = "Move the launcher to a folder of its own, for example C:\\Origins Conquer, and open it again.",
            LookingForUpdates = "Checking for updates...",
            ServerDown = "Update server unavailable.",
            ServerDownPlay = "You can play the installed version or try again with \"Verify files\".",
            CannotConnect = "Could not connect to the update server.",
            CannotConnectDetail = "Check your internet connection and click \"Verify files\" to try again.",
            Ready = "Ready to play!", FilesUpdated = "{0} file(s) updated.", UpToDate = "Your game is up to date.",
            GameOpen = "The game is running.", UpdateError = "Error updating the game.",
            ServerSlow = "The server took too long to answer.", TryAgain = "Click \"Verify files\" to try again.",
            CannotStart = "Could not start the game.",
            Subtitle = "CLASSIC CONQUER  ·  PATCH 5095", News = "NEWS",
            Welcome = "Welcome to Origins Conquer! Server news shows up here.",
            ClientVersion = "Client v{0}", DateFormat = "MM/dd/yyyy",
            VerifyingFiles = "Verifying files...", VerifyingDetail = "{0} of {1}: {2}",
            Downloading = "Downloading the game... {0} of {1} files", Retry = "Temporary failure, trying again...",
            DownloadingPack = "Downloading the game...", Unpacking = "Installing the game... {0} of {1} files",
            UpdatingLauncher = "Updating the launcher...",
            BadLauncher = "The downloaded launcher is corrupted. Please try again.",
            BadManifest = "Invalid update manifest.",
            Corrupted = "file corrupted while downloading.", ConnInterrupted = "The connection was interrupted.",
            ConnStalled = "The connection stopped responding.",
            CannotReplace = "Could not replace {0}. Close the game and try again.",
            Of = "{0} of {1}", Remaining = "{0} left",
            InstallTitle = "Install Origins Conquer", InstallHeading = "Where do you want to install the game?",
            InstallInfo = "The launcher downloads the game into this folder (about 450 MB to download, 1.3 GB installed), creates desktop and Start menu shortcuts, adds Origins Conquer to Windows' installed apps (to uninstall) and, if Windows has no Flash, registers the game's Flash component for your user.",
            UninstallTitle = "Uninstall Origins Conquer",
            UninstallConfirm = "Remove Origins Conquer from this computer?\n\nThe game files in {0}, the shortcuts and the launcher will be deleted. Files you added to the folder are kept.",
            UninstallGameOpen = "Close the game before uninstalling.",
            UninstallDone = "Origins Conquer was removed.",
            Browse = "Browse...", Install = "Install", Cancel = "Cancel", PickFolder = "Choose the install folder",
            InstallFailed = "Could not install here: {0}"
        };

        public static readonly Strings Pt = new Strings
        {
            Code = "pt", Name = "Português (Brasil)",
            Play = "JOGAR", Site = "Site", Check = "Verificar arquivos", Language = "Idioma", Starting = "Iniciando...",
            NoWrite = "Sem permissão para gravar nesta pasta.",
            NoWriteDetail = "Mova o launcher para uma pasta própria, por exemplo C:\\Origins Conquer, e abra de novo.",
            LookingForUpdates = "Procurando atualizações...",
            ServerDown = "Servidor de atualizações indisponível.",
            ServerDownPlay = "Você pode jogar com a versão instalada ou tentar de novo em \"Verificar arquivos\".",
            CannotConnect = "Não foi possível conectar ao servidor de atualizações.",
            CannotConnectDetail = "Verifique sua internet e clique em \"Verificar arquivos\" para tentar de novo.",
            Ready = "Pronto para jogar!", FilesUpdated = "{0} arquivo(s) atualizado(s).", UpToDate = "Seu jogo está atualizado.",
            GameOpen = "O jogo está aberto.", UpdateError = "Erro ao atualizar o jogo.",
            ServerSlow = "O servidor demorou para responder.", TryAgain = "Clique em \"Verificar arquivos\" para tentar de novo.",
            CannotStart = "Não foi possível abrir o jogo.",
            Subtitle = "CONQUER CLÁSSICO  ·  VERSÃO 5095", News = "NOTÍCIAS",
            Welcome = "Bem-vindo ao Origins Conquer! Novidades do servidor aparecem aqui.",
            ClientVersion = "Cliente v{0}", DateFormat = "dd/MM/yyyy",
            VerifyingFiles = "Verificando arquivos...", VerifyingDetail = "{0} de {1}: {2}",
            Downloading = "Baixando o jogo... {0} de {1} arquivos", Retry = "Falha temporária, tentando de novo...",
            DownloadingPack = "Baixando o jogo...", Unpacking = "Instalando o jogo... {0} de {1} arquivos",
            UpdatingLauncher = "Atualizando o launcher...",
            BadLauncher = "O launcher baixado está corrompido. Tente novamente.",
            BadManifest = "Manifesto de atualização inválido.",
            Corrupted = "arquivo corrompido no download.", ConnInterrupted = "A conexão foi interrompida.",
            ConnStalled = "A conexão parou de responder.",
            CannotReplace = "Não foi possível substituir {0}. Feche o jogo e tente de novo.",
            Of = "{0} de {1}", Remaining = "{0} restantes",
            InstallTitle = "Instalar Origins Conquer", InstallHeading = "Onde instalar o jogo?",
            InstallInfo = "O launcher baixa o jogo para esta pasta (cerca de 450 MB de download, 1,3 GB instalado), cria atalhos na área de trabalho e no menu Iniciar, adiciona o Origins Conquer aos aplicativos instalados do Windows (para desinstalar) e, se o Windows não tiver Flash, registra o componente Flash do jogo para o seu usuário.",
            UninstallTitle = "Desinstalar Origins Conquer",
            UninstallConfirm = "Remover o Origins Conquer deste computador?\n\nOs arquivos do jogo em {0}, os atalhos e o launcher serão apagados. Arquivos que você colocou na pasta são mantidos.",
            UninstallGameOpen = "Feche o jogo antes de desinstalar.",
            UninstallDone = "O Origins Conquer foi removido.",
            Browse = "Procurar...", Install = "Instalar", Cancel = "Cancelar", PickFolder = "Escolha a pasta de instalação",
            InstallFailed = "Não foi possível instalar aqui: {0}"
        };

        public static readonly Strings Fil = new Strings
        {
            Code = "fil", Name = "Filipino",
            Play = "LARO", Site = "Website", Check = "I-check ang files", Language = "Wika", Starting = "Sinisimulan...",
            NoWrite = "Walang permiso na mag-save sa folder na ito.",
            NoWriteDetail = "Ilipat ang launcher sa sarili nitong folder, halimbawa C:\\Origins Conquer, at buksan ulit.",
            LookingForUpdates = "Naghahanap ng updates...",
            ServerDown = "Hindi maabot ang update server.",
            ServerDownPlay = "Puwede kang maglaro gamit ang naka-install na bersyon o subukan ulit sa \"I-check ang files\".",
            CannotConnect = "Hindi makakonekta sa update server.",
            CannotConnectDetail = "I-check ang internet mo at i-click ang \"I-check ang files\" para subukan ulit.",
            Ready = "Handa nang maglaro!", FilesUpdated = "{0} file ang na-update.", UpToDate = "Updated na ang laro mo.",
            GameOpen = "Bukas pa ang laro.", UpdateError = "May error sa pag-update ng laro.",
            ServerSlow = "Masyadong matagal sumagot ang server.", TryAgain = "I-click ang \"I-check ang files\" para subukan ulit.",
            CannotStart = "Hindi mabuksan ang laro.",
            Subtitle = "CLASSIC CONQUER  ·  PATCH 5095", News = "BALITA",
            Welcome = "Maligayang pagdating sa Origins Conquer! Dito lalabas ang mga balita ng server.",
            ClientVersion = "Client v{0}", DateFormat = "MM/dd/yyyy",
            VerifyingFiles = "Vine-verify ang files...", VerifyingDetail = "{0} sa {1}: {2}",
            Downloading = "Dina-download ang laro... {0} sa {1} files", Retry = "Pansamantalang error, sinusubukan ulit...",
            DownloadingPack = "Dina-download ang laro...", Unpacking = "Ini-install ang laro... {0} sa {1} files",
            UpdatingLauncher = "Ina-update ang launcher...",
            BadLauncher = "Sira ang na-download na launcher. Subukan ulit.",
            BadManifest = "Hindi valid ang update manifest.",
            Corrupted = "nasira ang file habang dina-download.", ConnInterrupted = "Naputol ang koneksyon.",
            ConnStalled = "Huminto sa pagsagot ang koneksyon.",
            CannotReplace = "Hindi mapalitan ang {0}. Isara ang laro at subukan ulit.",
            Of = "{0} sa {1}", Remaining = "{0} na lang",
            InstallTitle = "I-install ang Origins Conquer", InstallHeading = "Saan mo gustong i-install ang laro?",
            InstallInfo = "Ida-download ng launcher ang laro sa folder na ito (mga 450 MB na download, 1.3 GB kapag naka-install), gagawa ng shortcut sa desktop at sa Start menu, idadagdag ang Origins Conquer sa mga naka-install na app ng Windows (para ma-uninstall) at, kung walang Flash ang Windows, ire-register ang Flash component ng laro para sa user mo.",
            UninstallTitle = "I-uninstall ang Origins Conquer",
            UninstallConfirm = "Alisin ang Origins Conquer sa computer na ito?\n\nBuburahin ang mga file ng laro sa {0}, ang mga shortcut at ang launcher. Hindi gagalawin ang mga file na idinagdag mo sa folder.",
            UninstallGameOpen = "Isara muna ang laro bago mag-uninstall.",
            UninstallDone = "Naalis na ang Origins Conquer.",
            Browse = "Mag-browse...", Install = "I-install", Cancel = "Kanselahin", PickFolder = "Piliin ang folder ng install",
            InstallFailed = "Hindi ma-install dito: {0}"
        };

        public static readonly Strings[] All = {En, Pt, Fil};

        public static Strings For(string code)
        {
            foreach (var s in All)
                if (s.Code == code)
                    return s;
            return En;
        }

        /// <summary>Windows display language: Portuguese, Filipino/Tagalog, otherwise English.</summary>
        public static string SystemLanguage()
        {
            string iso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (iso == "pt") return "pt";
            if (iso == "fil" || iso == "tl") return "fil";
            return "en";
        }
    }

    /// <summary>The launcher's current language.</summary>
    internal static class L
    {
        public static Strings S = Strings.For(Strings.SystemLanguage());
    }

    /// <summary>.originsupdate\settings.json: the player's choices.</summary>
    [DataContract]
    internal sealed class Settings
    {
        [DataMember(Name = "language")] public string Language { get; set; }

        public static Settings Load(Config config)
        {
            try
            {
                string path = PathOf(config);
                if (File.Exists(path))
                    return Json.Parse<Settings>(File.ReadAllBytes(path)) ?? new Settings();
            }
            catch (System.Exception)
            {
                // unreadable settings: defaults
            }

            return new Settings();
        }

        public void Save(Config config)
        {
            Directory.CreateDirectory(config.StateDir);
            File.WriteAllBytes(PathOf(config), Json.Serialize(this));
        }

        private static string PathOf(Config config) => Path.Combine(config.StateDir, "settings.json");
    }
}
