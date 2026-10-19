namespace Lifestream;

// Game text first; the literal lists are the strings that used to be matched, kept verbatim as the fallback.
// Quest dialogue rows are found by key because their row ids differ between game versions.
internal static partial class Lang
{
    internal static readonly GameTextMatcher EnterWorkshop = GameTextMatcher.Exact(nameof(EnterWorkshop),
        [GameTextSource.Dialogue("custom/001/CmnDefHousingPersonalRoomEntrance_00178", "TEXT_CMNDEFHOUSINGPERSONALROOMENTRANCE_00178_GOTO_WORKSHOP")],
        ["Move to the company workshop", "地下工房に移動する", "移动到部队工房", "移動到部隊工房", "移動到公會工坊", "Die Gesellschaftswerkstätte betreten", "Aller dans l'atelier de compagnie", "지하공방으로 이동"]);

    internal static readonly GameTextMatcher LogInPartialText = GameTextMatcher.Template(nameof(LogInPartialText),
        [GameTextSource.LobbyRow(25), GameTextSource.LobbyRow(95), GameTextSource.LobbyRow(96), GameTextSource.LobbyRow(629)],
        ["Logging in with", "Log in with", "でログインします。", "einloggen?", "eingeloggt.", "Se connecter avec", "Vous allez vous connecter avec", "Souhaitez-vous vous connecter avec", "登入吗？", "登入嗎？", "접속하시겠습니까?", "登錄嗎", "登录吗"]);

    internal static readonly GameTextMatcher Aethernet = GameTextMatcher.Exact(nameof(Aethernet),
        [GameTextSource.Dialogue("transport/Aetheryte", "TEXT_AETHERYTE_TOWN_WARP")],
        ["Aethernet.", "都市転送網", "Ätherytennetz", "Réseau de transport urbain éthéré", "都市传送网", "都市傳送網", "도시 내 이동"]);

    internal static readonly GameTextMatcher VisitAnotherWorld = GameTextMatcher.Exact(nameof(VisitAnotherWorld),
        [GameTextSource.Dialogue("transport/Aetheryte", "TEXT_AETHERYTE_MENU_WKT")],
        ["Visit Another World Server.", "他のワールドへ遊びにいく", "Weltenreise", "Voyager vers un autre Monde", "跨界传送（前往其他服务器游玩）", "跨界傳送（前往其他服務器遊玩）", "跨界傳送", "다른 서버 방문(서버 텔레포)"]);

    internal static readonly GameTextMatcher ConfirmWorldVisit = GameTextMatcher.Template(nameof(ConfirmWorldVisit),
        [GameTextSource.AddonRow(12519), GameTextSource.AddonRow(12624)],
        ["Travel to", "へ移動します、よろしいですか？", "reisen?", "Voulez-vous vraiment visiter", "确定要移动到", "確定要移動到", "방문하시겠습니까?"]);

    internal static readonly GameTextMatcher TravelToFirmament = GameTextMatcher.Exact(nameof(TravelToFirmament),
        [GameTextSource.Dialogue("transport/AetheryteIshgard", "TEXT_AETHERYTEISHGARD_HWD_WARP")],
        ["Travel to the Firmament.", "蒼天街転送", "Himmelsstadt", "Azurée", "传送到天穹街", "傳送到蒼天街", "창천 거리 이동"]);

    internal static readonly GameTextMatcher ResidentialDistrict = GameTextMatcher.Exact(nameof(ResidentialDistrict),
        [GameTextSource.Dialogue("transport/Aetheryte", "TEXT_AETHERYTE_HOUSING_WARP")],
        ["Residential District Aethernet.", "冒険者居住区転送", "Wohngebiet", "Quartier résidentiel", "冒险者住宅区传送", "冒險者住宅區傳送", "모험가 거주구 이동"]);

    internal static readonly GameTextMatcher GoToWard = GameTextMatcher.Exact(nameof(GoToWard),
        [GameTextSource.AddonRow(6349)],
        ["Go to specified ward. (Review Tabs)", "区を指定して移動（ハウスアピール確認）", "Zum angegebenen Bezirk (Zweck der Unterkunft einsehen)", "Spécifier le secteur où aller (Voir les attraits)", "移动到指定小区（查看房屋宣传标签）", "移動到指定小區（查看房屋宣傳標籤）", "移動到指定社區（查看房屋宣傳標籤）", "구역을 지정하여 이동(주택 정보 확인)"]);

    internal static readonly GameTextMatcher TravelTo = GameTextMatcher.Template(nameof(TravelTo),
        [GameTextSource.AddonRow(6355)],
        ["Travel to", "よろしいですか？", "Zu Wohnbezirk", "Vous allez vous rendre à", "要移动到", "要移動到", "이동하시겠습니까?"]);

    internal static readonly GameTextMatcher GoToSpecifiedApartment = GameTextMatcher.Exact(nameof(GoToSpecifiedApartment),
        [GameTextSource.Dialogue("custom/003/HouFixMansionEntrance_00359", "TEXT_HOUFIXMANSIONENTRANCE_00359_HOUSINGAREA_MENU_ENTER_ROOM")],
        ["Go to specified apartment", "Go to speciz`fied apartment", "部屋番号を指定して移動（ハウスアピール確認）", "Eine bestimmte Wohnung betreten (Zweck der Unterkunft einsehen)", "移动到指定号码房间（查看房屋宣传标签）", "移動到指定號碼房間（查看房屋宣傳標籤）", "방 번호를 지정하여 이동(주택 정보 확인)", "Spécifier l'appartement où aller (Voir les attraits)"]);

    internal static readonly GameTextMatcher EnterApartmenr = GameTextMatcher.Template(nameof(EnterApartmenr),
        [GameTextSource.AddonRow(6782), GameTextSource.AddonRow(6784)],
        ["Enter", "よろしいですか？", "betreten?", "Aller dans l'appartement", "要移动到", "要移動到", "이동하시겠습니까?"]);

    internal static readonly GameTextMatcher GoToMyApartment = GameTextMatcher.Exact(nameof(GoToMyApartment),
        [GameTextSource.Dialogue("custom/003/HouFixMansionEntrance_00359", "TEXT_HOUFIXMANSIONENTRANCE_00359_HOUSINGAREA_MENU_ENTER_MYROOM")],
        ["Go to your apartment", "移动到自己的房间", "移動到自己的房間", "自分の部屋に移動する", "자신의 방으로 이동", "Aller dans votre appartement"]);

    internal static readonly GameTextMatcher TravelToInstancedArea = GameTextMatcher.Contains(nameof(TravelToInstancedArea),
        [GameTextSource.Dialogue("transport/Aetheryte", "TEXT_AETHERYTE_MOVE_INSTANCE")],
        ["Travel to Instanced Area.", "インスタンスエリアへ移動", "In ein instanziiertes Areal wechseln", "Changer d'instance", "切换副本区", "切換副本區", "인스턴스 지역으로 이동"]);

    internal static readonly GameTextMatcher TravelToYourIsland = GameTextMatcher.Contains(nameof(TravelToYourIsland),
        [GameTextSource.Dialogue("custom/007/CtsMjiEntrance_00798", "TEXT_CTSMJIENTRANCE_00798_SYSTEM_000_005")],
        ["Travel to your island?", "あなたの島へ向かいますか？", "Zu deiner Insel fahren?", "Voulez-vous aller sur votre île?", "나의 섬으로 가시겠습니까?", "要前往你的小岛吗？", "要前往你的小島嗎?"]);

    internal static readonly GameTextMatcher TravelToMyIsland = GameTextMatcher.Exact(nameof(TravelToMyIsland),
        [GameTextSource.Dialogue("custom/007/CtsMjiEntrance_00798", "TEXT_CTSMJIENTRANCE_00798_Q1_000_000")],
        ["Travel to my island.", "「自分の島」に行く", "Zur eigenen Insel fahren", "Aller sur son île", "'나의 섬'으로 가기", "前往自己的小岛", "前往自己的小島"]);

    internal static readonly GameTextMatcher Entrance = GameTextMatcher.Exact(nameof(Entrance),
        [GameTextSource.EObjNameRow(2002737), GameTextSource.EObjNameRow(2002739)],
        ["ハウスへ入る", "进入房屋", "進入房屋", "Eingang", "Entrée", "Entrance", "주택으로 들어가기"]);

    internal static readonly GameTextMatcher ConfirmHouseEntrance = GameTextMatcher.Exact(nameof(ConfirmHouseEntrance),
        [GameTextSource.WarpQuestion(131148)],
        ["「ハウス」へ入りますか？", "要进入这间房屋吗？", "要進入這間房屋嗎？", "Das Gebäude betreten?", "Entrer dans la maison ?", "Enter the estate hall?", "'주택'으로 들어가시겠습니까?"]);

    internal static readonly GameTextMatcher UnableToSelectWorldForDcv = GameTextMatcher.Template(nameof(UnableToSelectWorldForDcv),
        [GameTextSource.LobbyRow(1218)],
        ["Unable to select", "で選択したワールド", "所选择的服务器", "所選擇的服務器", "Die für die", "pas être choisi comme destination pour", "의 목적지로 선택할 수 없습니다"]);

    // Prompt of the instance list. The old check compared the whole prompt with this row minus its zone-name macro,
    // which the menu always fills in, so it never matched: there is no built-in text to fall back to.
    internal static readonly GameTextMatcher ToReduceCongestion = GameTextMatcher.Template(nameof(ToReduceCongestion),
        [GameTextSource.AddonRow(2090)],
        []);
}
