using System.Collections.Generic;

namespace HSAEnhanced.Core
{
    // Our own wording for the few texts the core needs that the game's tables lack. Used only when
    // Hearthstone Access (whose table normally has them) is not installed. English only.
    static class OwnStrings
    {
        static readonly Dictionary<string, string> Texts = new Dictionary<string, string>
        {
            { "ACCESSIBILITY_FORMATTING_PERIOD", "." },
            { "ACCESSIBILITY_FORMATTING_SENTENCE_ENDING_CHARACTERS", ".!?;" },
            { "ACCESSIBILITY_FORMATTING_LIST_SEPARATOR", "," },
            { "ACCESSIBILITY_FORMATTING_LIST_FINAL_SEPARATOR", "and" },
            { "ACCESSIBILITY_INPUT_COMMAND_WITH_CTRL_FORMAT", "Control {0}" },
            { "ACCESSIBILITY_INPUT_COMMAND_WITH_MODIFIER_FORMAT", "Shift {0}" },
            { "ACCESSIBILITY_INPUT_KEY_OVERRIDE_Return", "Enter" },
            { "ACCESSIBILITY_LIST_NO_ITEMS", "Empty" },
            { "ACCESSIBILITY_MENU_OPTION_FORMAT", "{0}, {1} of {2}" },
            { "ACCESSIBILITY_MENU_HELP_NO_BACK_BUTTON", "Up and down arrows move through the list. {0} chooses" },
            { "ACCESSIBILITY_MENU_HELP_WITH_BACK_BUTTON", "Up and down arrows move through the list. {0} chooses, {1} returns" },
            { "ACCESSIBILITY_PRESS_KEY_TO_GO_BACK", "{0} returns" },
            { "ACCESSIBILITY_OPTIONS_MENU_CHECKBOX_CHECKED", "on" },
            { "ACCESSIBILITY_OPTIONS_MENU_CHECKBOX_NOT_CHECKED", "off" },
            { "ACCESSIBILITY_SCREEN_MISSION_COMPLETED", "Done" },
            { "ACCESSIBILITY_SCREEN_MISSION_LOCKED", "Locked" },
            { "ACCESSIBILITY_UI_SOCIAL_MENU_NAME", "Friends" },
            { "ACCESSIBILITY_HUB_MAIN_MENU_TITLE", "Main menu" },
            { "ACCESSIBILITY_GLOBAL_SWITCH_FORMAT", "Format" },
            { "ACCESSIBILITY_UI_SOCIAL_BATTLETAG_COPIED_TO_CLIPBOARD", "Copied" },
            { "ACCESSIBILITY_UI_SOCIAL_CHAT_MESSAGE_RECEIVED", "{0} said: {1}" },
            { "ACCESSIBILITY_UI_SOCIAL_CHAT_MESSAGE_SENT", "You said to {0}: {1}" },
            { "ACCESSIBILITY_UI_SOCIAL_CHAT_SEND_MESSAGE", "Write a message" },
            { "ACCESSIBILITY_UI_SOCIAL_CHAT_TYPE_MESSAGE_PROMPT", "Message. Enter sends, Escape cancels" },
            { "ACCESSIBILITY_UI_SOCIAL_INVITE_FRIEND_TO_BG", "Invite to your Battlegrounds party" },
        };

        // Hearthstone Access's own table (Strings/<language>/ACCESSIBILITY.txt, the game's file format),
        // which the installer puts in the game folder from its download also when HSA itself is not
        // installed: the game does not load it then, so we read it (English under the player's language)
        static Dictionary<string, string> s_table;

        static Dictionary<string, string> Table()
        {
            if (s_table != null) return s_table;
            s_table = new Dictionary<string, string>();
            Load(Locale.enUS);
            try { var locale = Localization.GetLocale(); if (locale != Locale.enUS) Load(locale); } catch (System.Exception e) { Log.Error(e); }
            Log.Info("strings: " + s_table.Count + " texts of Hearthstone Access's table");
            return s_table;
        }

        static void Load(Locale locale)
        {
            try
            {
                var path = GameStrings.GetAssetPath(locale, "ACCESSIBILITY.txt");
                if (!System.IO.File.Exists(path)) return;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var tab = line.IndexOf('\t');
                    if (tab <= 0 || line.StartsWith("TAG\t") || line.StartsWith("#")) continue;
                    var end = line.IndexOf('\t', tab + 1);
                    var text = end < 0 ? line.Substring(tab + 1) : line.Substring(tab + 1, end - tab - 1);
                    if (text.Length == 0) continue;
                    s_table[line.Substring(0, tab)] = text.Replace("\\n", "\n");
                }
            }
            catch (System.Exception e) { Log.Error(e); }
        }

        // texts only this mod uses (no table of the game or of Hearthstone Access has them), in every
        // language the game offers; by language code (zhTW its own), English for any other
        static Dictionary<string, string> L(params string[] pairs)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[pairs[i]] = pairs[i + 1];
            return d;
        }

        static readonly Dictionary<string, Dictionary<string, string>> Ours = new Dictionary<string, Dictionary<string, string>>
        {
            { "ACCESSIBILITY_INPUT_KEY_OVERRIDE_LeftBracket", L(
                "en", "Left bracket", "pl", "Lewy nawias kwadratowy", "de", "Eckige Klammer links", "es", "Corchete izquierdo",
                "fr", "Crochet gauche", "it", "Parentesi quadra sinistra", "pt", "Colchete esquerdo", "ru", "Левая квадратная скобка",
                "ja", "左角かっこ", "ko", "왼쪽 대괄호", "zh", "左方括号", "zhTW", "左方括號", "th", "วงเล็บเหลี่ยมซ้าย") },
            { "ACCESSIBILITY_INPUT_KEY_OVERRIDE_RightBracket", L(
                "en", "Right bracket", "pl", "Prawy nawias kwadratowy", "de", "Eckige Klammer rechts", "es", "Corchete derecho",
                "fr", "Crochet droit", "it", "Parentesi quadra destra", "pt", "Colchete direito", "ru", "Правая квадратная скобка",
                "ja", "右角かっこ", "ko", "오른쪽 대괄호", "zh", "右方括号", "zhTW", "右方括號", "th", "วงเล็บเหลี่ยมขวา") },
            { "ACCESSIBILITY_INPUT_KEY_OVERRIDE_Semicolon", L(
                "en", "Semicolon", "pl", "Średnik", "de", "Semikolon", "es", "Punto y coma", "fr", "Point-virgule", "it", "Punto e virgola",
                "pt", "Ponto e vírgula", "ru", "Точка с запятой", "ja", "セミコロン", "ko", "세미콜론", "zh", "分号", "zhTW", "分號", "th", "อัฒภาค") },
            { "HSA_ENHANCED_ASSEMBLE_PARTS_HELP", L(
                "en", "{0} and {1} move between cards. {2} and {3} read the selected card. {4} selects its top part; {5} selects its bottom part. {6} reads the assembled target. {7} confirms it.",
                "pl", "{0} i {1} przechodzą między kartami. {2} i {3} czytają wybraną kartę. {4} wybiera jej górną część, {5} dolną część. {6} czyta złożoną kartę. {7} ją zatwierdza.",
                "de", "{0} und {1} wechseln zwischen den Karten. {2} und {3} lesen die gewählte Karte vor. {4} wählt ihren oberen Teil, {5} ihren unteren Teil. {6} liest die zusammengesetzte Karte vor. {7} bestätigt sie.",
                "es", "{0} y {1} cambian de carta. {2} y {3} leen la carta seleccionada. {4} elige su parte superior; {5}, su parte inferior. {6} lee la carta ensamblada. {7} la confirma.",
                "fr", "{0} et {1} passent d'une carte à l'autre. {2} et {3} lisent la carte sélectionnée. {4} choisit sa partie supérieure ; {5}, sa partie inférieure. {6} lit la carte assemblée. {7} la confirme.",
                "it", "{0} e {1} passano da una carta all'altra. {2} e {3} leggono la carta selezionata. {4} sceglie la parte superiore; {5} la parte inferiore. {6} legge la carta assemblata. {7} la conferma.",
                "pt", "{0} e {1} alternam entre as cartas. {2} e {3} leem a carta selecionada. {4} escolhe a parte de cima; {5}, a parte de baixo. {6} lê a carta montada. {7} a confirma.",
                "ru", "{0} и {1} переключают карты. {2} и {3} читают выбранную карту. {4} выбирает её верхнюю часть, {5} — нижнюю. {6} читает собранную карту. {7} подтверждает её.",
                "ja", "{0}と{1}でカードを切り替えます。{2}と{3}で選択中のカードを読み上げます。{4}で上部を、{5}で下部を選びます。{6}で組み立てたカードを読み上げ、{7}で確定します。",
                "ko", "{0}와 {1}(으)로 카드를 이동합니다. {2}와 {3}(으)로 선택한 카드를 읽습니다. {4}(으)로 윗부분을, {5}(으)로 아랫부분을 선택합니다. {6}(으)로 조립된 카드를 읽고 {7}(으)로 확정합니다.",
                "zh", "{0}和{1}在卡牌间切换。{2}和{3}朗读所选卡牌。{4}选择其上半部分，{5}选择其下半部分。{6}朗读组装后的卡牌，{7}确认。",
                "zhTW", "{0}和{1}在卡牌間切換。{2}和{3}朗讀所選卡牌。{4}選擇其上半部分，{5}選擇其下半部分。{6}朗讀組裝後的卡牌，{7}確認。",
                "th", "{0} และ {1} สลับระหว่างการ์ด {2} และ {3} อ่านการ์ดที่เลือก {4} เลือกส่วนบน {5} เลือกส่วนล่าง {6} อ่านการ์ดที่ประกอบแล้ว {7} ยืนยัน") },
            { "HSA_ENHANCED_ASSEMBLE_TARGET_HELP", L(
                "en", "{0} and {1} read the assembled target. {2} returns to the card parts. {3} confirms the assembly.",
                "pl", "{0} i {1} czytają złożoną kartę. {2} wraca do części kart. {3} zatwierdza złożenie.",
                "de", "{0} und {1} lesen die zusammengesetzte Karte vor. {2} kehrt zu den Kartenteilen zurück. {3} bestätigt die Zusammensetzung.",
                "es", "{0} y {1} leen la carta ensamblada. {2} vuelve a las partes de la carta. {3} confirma el ensamblaje.",
                "fr", "{0} et {1} lisent la carte assemblée. {2} revient aux parties de carte. {3} confirme l'assemblage.",
                "it", "{0} e {1} leggono la carta assemblata. {2} torna alle parti della carta. {3} conferma l'assemblaggio.",
                "pt", "{0} e {1} leem a carta montada. {2} volta às partes da carta. {3} confirma a montagem.",
                "ru", "{0} и {1} читают собранную карту. {2} возвращает к частям карт. {3} подтверждает сборку.",
                "ja", "{0}と{1}で組み立てたカードを読み上げます。{2}でカードパーツに戻ります。{3}で組み立てを確定します。",
                "ko", "{0}와 {1}(으)로 조립된 카드를 읽습니다. {2}(으)로 카드 부분으로 돌아갑니다. {3}(으)로 조립을 확정합니다.",
                "zh", "{0}和{1}朗读组装后的卡牌。{2}返回卡牌部件。{3}确认组装。",
                "zhTW", "{0}和{1}朗讀組裝後的卡牌。{2}返回卡牌部件。{3}確認組裝。",
                "th", "{0} และ {1} อ่านการ์ดที่ประกอบแล้ว {2} กลับไปยังส่วนของการ์ด {3} ยืนยันการประกอบ") },
            { "ACCESSIBILITY_GAMEPLAY_QUERY_ASSEMBLE", L(
                "en", "Assemble a card. Current target: {0}", "pl", "Złóż kartę. Obecnie: {0}", "de", "Setze eine Karte zusammen. Aktuell: {0}",
                "es", "Ensambla una carta. Actual: {0}", "fr", "Assemblez une carte. Actuellement : {0}", "it", "Assembla una carta. Attuale: {0}",
                "pt", "Monte uma carta. Atual: {0}", "ru", "Соберите карту. Сейчас: {0}", "ja", "カードを組み立てます。現在: {0}",
                "ko", "카드를 조립하세요. 현재: {0}", "zh", "组装一张卡牌。当前：{0}", "zhTW", "組裝一張卡牌。目前：{0}", "th", "ประกอบการ์ด ปัจจุบัน: {0}") },
            { "ACCESSIBILITY_GAMEPLAY_ASSEMBLE_NO_SELECTION", L(
                "en", "Select a card part before confirming", "pl", "Przed zatwierdzeniem wybierz część karty", "de", "Wähle vor dem Bestätigen einen Kartenteil",
                "es", "Elige una parte de la carta antes de confirmar", "fr", "Choisissez une partie de carte avant de confirmer",
                "it", "Scegli una parte della carta prima di confermare", "pt", "Escolha uma parte da carta antes de confirmar",
                "ru", "Выберите часть карты перед подтверждением", "ja", "確定する前にカードパーツを選んでください", "ko", "확정하기 전에 카드 부분을 선택하세요",
                "zh", "确认前请先选择卡牌部件", "zhTW", "確認前請先選擇卡牌部件", "th", "เลือกส่วนของการ์ดก่อนยืนยัน") },
            { "HSA_ENHANCED_ASSEMBLE_CANNOT_CONFIRM", L(
                "en", "The assembly cannot be confirmed right now", "pl", "Nie można teraz zatwierdzić złożenia", "de", "Die Zusammensetzung kann gerade nicht bestätigt werden",
                "es", "No se puede confirmar el ensamblaje ahora", "fr", "L'assemblage ne peut pas être confirmé pour le moment",
                "it", "Non è possibile confermare l'assemblaggio ora", "pt", "Não é possível confirmar a montagem agora", "ru", "Сейчас нельзя подтвердить сборку",
                "ja", "今は組み立てを確定できません", "ko", "지금은 조립을 확정할 수 없습니다", "zh", "现在无法确认组装", "zhTW", "現在無法確認組裝",
                "th", "ไม่สามารถยืนยันการประกอบได้ในตอนนี้") },
            { "HSA_ENHANCED_ASSEMBLE_PART_UNAVAILABLE", L(
                "en", "That card part is unavailable", "pl", "Ta część karty jest niedostępna", "de", "Dieser Kartenteil ist nicht verfügbar",
                "es", "Esa parte de la carta no está disponible", "fr", "Cette partie de carte n'est pas disponible", "it", "Questa parte della carta non è disponibile",
                "pt", "Essa parte da carta não está disponível", "ru", "Эта часть карты недоступна", "ja", "そのカードパーツは使用できません",
                "ko", "그 카드 부분은 사용할 수 없습니다", "zh", "该卡牌部件不可用", "zhTW", "該卡牌部件無法使用", "th", "ส่วนของการ์ดนั้นใช้ไม่ได้") },
            { "HSA_ENHANCED_ADVENTURE_BOOK_HELP", L(
                "en", "Up and down arrows move through the list, {0} chooses. {1} and {2} turn the book's pages. {3} goes back to the chapters.",
                "pl", "Strzałki w górę i w dół poruszają po liście, {0} wybiera. {1} i {2} przewracają strony księgi. {3} wraca do spisu rozdziałów.",
                "de", "Die Pfeiltasten nach oben und unten bewegen durch die Liste, {0} wählt aus. {1} und {2} blättern die Seiten des Buches um. {3} kehrt zu den Kapiteln zurück.",
                "es", "Las flechas arriba y abajo recorren la lista, {0} elige. {1} y {2} pasan las páginas del libro. {3} vuelve a los capítulos.",
                "fr", "Les flèches haut et bas parcourent la liste, {0} choisit. {1} et {2} tournent les pages du livre. {3} revient aux chapitres.",
                "it", "Le frecce su e giù scorrono l'elenco, {0} sceglie. {1} e {2} girano le pagine del libro. {3} torna ai capitoli.",
                "pt", "As setas para cima e para baixo percorrem a lista, {0} escolhe. {1} e {2} viram as páginas do livro. {3} volta aos capítulos.",
                "ru", "Стрелки вверх и вниз перемещают по списку, {0} выбирает. {1} и {2} листают страницы книги. {3} возвращает к главам.",
                "ja", "上下の矢印キーでリストを移動し、{0}で選択します。{1}と{2}で本のページをめくります。{3}で章の一覧に戻ります。",
                "ko", "위아래 화살표로 목록을 이동하고 {0}(으)로 선택합니다. {1}와 {2}(으)로 책장을 넘깁니다. {3}(으)로 장 목록으로 돌아갑니다.",
                "zh", "上下方向键浏览列表，{0}选择。{1}和{2}翻动书页。{3}返回章节列表。",
                "zhTW", "上下方向鍵瀏覽清單，{0}選擇。{1}和{2}翻動書頁。{3}返回章節清單。",
                "th", "ลูกศรขึ้นและลงเลื่อนไปในรายการ {0} เลือก {1} และ {2} พลิกหน้าหนังสือ {3} กลับไปยังรายการบท") },
            { "HSA_ENHANCED_ADVENTURE_CHAPTERS", L(
                "en", "Chapters", "pl", "Rozdziały", "de", "Kapitel", "es", "Capítulos", "fr", "Chapitres", "it", "Capitoli",
                "pt", "Capítulos", "ru", "Главы", "ja", "章", "ko", "장", "zh", "章节", "zhTW", "章節", "th", "บท") },
            { "HSA_ENHANCED_ADVENTURE_NO_MORE_PAGES", L(
                "en", "No more pages", "pl", "Nie ma więcej stron", "de", "Keine weiteren Seiten", "es", "No hay más páginas",
                "fr", "Plus de pages", "it", "Non ci sono altre pagine", "pt", "Não há mais páginas", "ru", "Больше страниц нет",
                "ja", "これ以上ページはありません", "ko", "더 이상 페이지가 없습니다", "zh", "没有更多页面", "zhTW", "沒有更多頁面", "th", "ไม่มีหน้าเพิ่มเติม") },
            { "HSA_ENHANCED_PREVIOUS_PAGE", L(
                "en", "Previous page", "pl", "Poprzednia strona", "de", "Vorherige Seite", "es", "Página anterior", "fr", "Page précédente",
                "it", "Pagina precedente", "pt", "Página anterior", "ru", "Предыдущая страница", "ja", "前のページ", "ko", "이전 페이지",
                "zh", "上一页", "zhTW", "上一頁", "th", "หน้าก่อนหน้า") },
            { "HSA_ENHANCED_NEXT_PAGE", L(
                "en", "Next page", "pl", "Następna strona", "de", "Nächste Seite", "es", "Página siguiente", "fr", "Page suivante",
                "it", "Pagina successiva", "pt", "Próxima página", "ru", "Следующая страница", "ja", "次のページ", "ko", "다음 페이지",
                "zh", "下一页", "zhTW", "下一頁", "th", "หน้าถัดไป") },
            { "HSA_ENHANCED_SHOP_QUANTITY", L(
                "en", "Quantity: {0}", "pl", "Ilość: {0}", "de", "Menge: {0}", "es", "Cantidad: {0}", "fr", "Quantité : {0}",
                "it", "Quantità: {0}", "pt", "Quantidade: {0}", "ru", "Количество: {0}", "ja", "数量: {0}", "ko", "수량: {0}",
                "zh", "数量：{0}", "zhTW", "數量：{0}", "th", "จำนวน: {0}") },
        };

        static string OursText(string key)
        {
            Dictionary<string, string> texts;
            if (!Ours.TryGetValue(key, out texts)) return null;
            string code = "en";
            try { code = Localization.GetLocale().ToString(); } catch { }
            string text;
            if (texts.TryGetValue(code, out text)) return text;
            if (code.Length >= 2 && texts.TryGetValue(code.Substring(0, 2), out text)) return text;
            return texts["en"];
        }

        // null when we have no text for it either
        internal static string Get(string key, params object[] args)
        {
            string text = OursText(key);
            if (text != null)
            {
                if (args == null || args.Length == 0) return text;
                try { return string.Format(text, args); } catch { return text; }
            }
            if (Table().TryGetValue(key, out text))
            {
                try { return args == null || args.Length == 0 ? GameStrings.ParseLanguageRules(text) : GameStrings.FormatLocalizedString(text, args); }
                catch { return text; }
            }
            if (!Texts.TryGetValue(key, out text)) return null;
            if (args == null || args.Length == 0) return text;
            try { return string.Format(text, args); } catch { return text; }
        }
    }
}
