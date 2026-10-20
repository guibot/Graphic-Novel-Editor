using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

// ---------------------------------------------------------------------------
// Formato exportado pelo Graphic Novel Creator. Os nomes dos campos têm de
// coincidir exatamente com o JSON (o JsonUtility não faz mapeamento).
// ---------------------------------------------------------------------------
[Serializable] public class GNCharacter { public string id, name, color, avatar; public bool isPlayer; }
[Serializable] public class GNOption { public string text, to; public List<string> events; }
[Serializable] public class GNNode
{
    public string id, type, character, kind, text, next;
    public List<string> events;
    public List<GNOption> options;
    public float x, y, w, h; // só layout do editor
}
[Serializable] public class GNStory
{
    public int version;
    public string start;
    public List<GNCharacter> characters;
    public List<GNNode> nodes;
}

// ---------------------------------------------------------------------------
// Dados que aparecem no Inspector
// ---------------------------------------------------------------------------
[Serializable] public class NamedSprite { public string name; public Sprite sprite; }
[Serializable] public class CharacterArt
{
    [Tooltip("Tem de ser igual ao id da personagem no JSON (ex.: astrid, player)")]
    public string id;
    public List<NamedSprite> expressions = new List<NamedSprite>();
}
[Serializable] public class StringEvent : UnityEvent<string> { }

/// <summary>
/// Leitor de novela gráfica. Monta o Canvas sozinho ao carregar em Play.
///
/// Eventos suportados (nos nós ou nas opções do editor):
///   fundo:NOME                         troca o fundo
///   mostrar:ID:SLOT:EXPRESSAO          mostra personagem (SLOT = esq | centro | dir)
///   expr:ID:EXPRESSAO                  muda a expressão de quem já está em cena
///   esconder:ID                        retira a personagem
/// Qualquer outro evento vai para "On Unhandled Event" (útil para sons, etc.).
///
/// Setup: GameObject vazio + este script + arrastar o story.json.
/// Depois, no "⋮" do componente: "Preencher a partir do JSON", e arrastar os PNGs.
/// </summary>
public class GraphicNovelPlayer : MonoBehaviour
{
    [Header("História")]
    public TextAsset storyJson;

    [Header("Imagens da cena")]
    public List<NamedSprite> backgrounds = new List<NamedSprite>();
    public List<CharacterArt> characters = new List<CharacterArt>();
    [Tooltip("Altura dos sprites das personagens, em pixels do canvas de referência (1920x1080)")]
    public float characterHeight = 1000f;
    [Tooltip("Escurece as personagens que não estão a falar")]
    public bool dimInactiveCharacters = true;

    [Header("Aspeto da UI (tudo opcional)")]
    public Sprite dialogueBoxSprite;
    public Sprite nameBoxSprite;
    public Sprite continueButtonSprite;
    public Sprite answerButtonSprite;
    public TMP_FontAsset font;
    public Color textColor = Color.white;
    public int nameFontSize = 48;
    public int textFontSize = 40;
    [Tooltip("Letras por segundo. 0 = aparece tudo de uma vez")]
    public float typewriterCharsPerSecond = 45f;

    [Header("Eventos")]
    public StringEvent onUnhandledEvent;

    // --- estado ---
    GNStory story;
    Dictionary<string, GNNode> nodes;
    Dictionary<string, GNCharacter> chars;
    Dictionary<string, Sprite> bgMap;
    Dictionary<string, CharacterArt> artMap;
    GNNode current;

    // --- UI ---
    static readonly string[] SlotNames = { "esq", "centro", "dir" };
    static readonly float[] SlotX = { 0.22f, 0.5f, 0.78f };
    Image bgImage;
    readonly Image[] slotImages = new Image[3];
    readonly string[] slotCharId = new string[3];
    TextMeshProUGUI nameText, bodyText;
    Button continueButton;
    RectTransform answersPanel;
    Coroutine typing;
    bool isTyping;

    // =======================================================================
    void Start()
    {
        if (storyJson == null) { Debug.LogError("GraphicNovelPlayer: falta arrastar o Story Json.", this); enabled = false; return; }
        story = JsonUtility.FromJson<GNStory>(storyJson.text);
        if (story == null || story.nodes == null || story.characters == null)
        {
            Debug.LogError("GraphicNovelPlayer: o JSON não tem o formato esperado.", this); enabled = false; return;
        }

        nodes = new Dictionary<string, GNNode>();
        foreach (var n in story.nodes) nodes[n.id] = n;
        chars = new Dictionary<string, GNCharacter>();
        foreach (var c in story.characters) chars[c.id] = c;

        bgMap = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in backgrounds)
            if (b != null && !string.IsNullOrEmpty(b.name) && b.sprite != null) bgMap[b.name.Trim()] = b.sprite;
        artMap = new Dictionary<string, CharacterArt>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in characters)
            if (c != null && !string.IsNullOrEmpty(c.id)) artMap[c.id.Trim()] = c;

        BuildUI();
        Show(story.start);
    }

    // =======================================================================
    //  Navegação
    // =======================================================================
    void Show(string id)
    {
        StopTyping();
        ClearAnswers();

        if (!string.IsNullOrEmpty(id) && !nodes.ContainsKey(id))
            Debug.LogWarning($"GraphicNovelPlayer: ligação para o nó '{id}' que não existe. A tratar como fim.", this);
        current = (!string.IsNullOrEmpty(id) && nodes.TryGetValue(id, out var n)) ? n : null;

        if (current == null) { ShowEnd(); return; }

        FireEvents(current.events, current.id);

        if (current.type == "fala")
        {
            string who = current.character;
            Color nameColor = textColor;
            if (chars.TryGetValue(current.character, out var c))
            {
                who = c.name;
                if (!string.IsNullOrEmpty(c.color) && ColorUtility.TryParseHtmlString(c.color, out var col)) nameColor = col;
            }
            nameText.text = who;
            nameText.color = nameColor;
            continueButton.gameObject.SetActive(true);
            ApplyDim(current.character);
            typing = StartCoroutine(TypeText(current.text));
        }
        else // "resposta": o texto da última fala fica visível por cima das opções
        {
            continueButton.gameObject.SetActive(false);
            ApplyDim(null);
            foreach (var o in current.options)
            {
                var opt = o;
                AddAnswerButton(opt.text, () => { FireEvents(opt.events, current.id); Show(opt.to); });
            }
        }
    }

    void ShowEnd()
    {
        nameText.text = "";
        bodyText.text = "FIM";
        bodyText.maxVisibleCharacters = 99999;
        continueButton.gameObject.SetActive(false);
        ApplyDim(null);
        AddAnswerButton("Recomeçar", Restart);
    }

    void Restart()
    {
        for (int i = 0; i < slotImages.Length; i++) ClearSlot(i);
        bgImage.sprite = null;
        bgImage.color = Color.black;
        Show(story.start);
    }

    void OnContinue()
    {
        if (current == null || current.type != "fala") return;
        if (isTyping) { FinishTyping(); return; }
        Show(current.next);
    }

    // =======================================================================
    //  Máquina de escrever
    // =======================================================================
    IEnumerator TypeText(string text)
    {
        bodyText.text = text;
        if (typewriterCharsPerSecond <= 0f) { bodyText.maxVisibleCharacters = 99999; typing = null; yield break; }

        bodyText.maxVisibleCharacters = 0;
        bodyText.ForceMeshUpdate();
        int total = bodyText.textInfo.characterCount;
        isTyping = true;
        float t = 0f;
        int shown = 0;
        while (shown < total)
        {
            t += Time.unscaledDeltaTime * typewriterCharsPerSecond;
            shown = Mathf.Min(total, (int)t);
            bodyText.maxVisibleCharacters = shown;
            yield return null;
        }
        bodyText.maxVisibleCharacters = 99999;
        isTyping = false;
        typing = null;
    }

    void FinishTyping()
    {
        StopTyping();
        bodyText.maxVisibleCharacters = 99999;
    }

    void StopTyping()
    {
        if (typing != null) StopCoroutine(typing);
        typing = null;
        isTyping = false;
    }

    // =======================================================================
    //  Eventos
    // =======================================================================
    void FireEvents(List<string> events, string nodeId)
    {
        if (events == null) return;
        foreach (var raw in events)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var p = raw.Split(':');
            string cmd = p[0].Trim().ToLowerInvariant();
            switch (cmd)
            {
                case "fundo":
                    if (p.Length < 2) Warn($"evento '{raw}' incompleto (esperado fundo:NOME)", nodeId);
                    else SetBackground(p[1].Trim(), nodeId);
                    break;
                case "mostrar":
                    if (p.Length < 4) Warn($"evento '{raw}' incompleto (esperado mostrar:ID:SLOT:EXPRESSAO)", nodeId);
                    else ShowCharacter(p[1].Trim(), p[2].Trim().ToLowerInvariant(), p[3].Trim(), nodeId);
                    break;
                case "expr":
                    if (p.Length < 3) Warn($"evento '{raw}' incompleto (esperado expr:ID:EXPRESSAO)", nodeId);
                    else SetExpression(p[1].Trim(), p[2].Trim(), nodeId);
                    break;
                case "esconder":
                    if (p.Length < 2) Warn($"evento '{raw}' incompleto (esperado esconder:ID)", nodeId);
                    else HideCharacter(p[1].Trim(), nodeId);
                    break;
                default:
                    Debug.Log($"GraphicNovelPlayer: evento não tratado '{raw}' (nó {nodeId})", this);
                    onUnhandledEvent?.Invoke(raw);
                    break;
            }
        }
    }

    void SetBackground(string name, string nodeId)
    {
        if (bgMap.TryGetValue(name, out var s))
        {
            bgImage.sprite = s;
            bgImage.color = Color.white;
        }
        else Warn($"fundo '{name}' não existe na lista Backgrounds (ou não tem sprite). Mantém o anterior.", nodeId);
    }

    void ShowCharacter(string id, string slotName, string expr, string nodeId)
    {
        int slot = Array.IndexOf(SlotNames, slotName);
        if (slot < 0) { Warn($"slot '{slotName}' inválido (usa esq, centro ou dir)", nodeId); return; }
        var s = FindExpression(id, expr);
        if (s == null) { Warn($"sem sprite para '{id}' / '{expr}'", nodeId); return; }

        for (int i = 0; i < slotCharId.Length; i++)
            if (i != slot && string.Equals(slotCharId[i], id, StringComparison.OrdinalIgnoreCase)) ClearSlot(i);

        slotCharId[slot] = id;
        ApplySprite(slot, s);
    }

    void SetExpression(string id, string expr, string nodeId)
    {
        int slot = FindSlot(id);
        if (slot < 0) { Warn($"'{id}' não está em cena (usa mostrar: primeiro)", nodeId); return; }
        var s = FindExpression(id, expr);
        if (s == null) { Warn($"sem sprite para '{id}' / '{expr}'", nodeId); return; }
        ApplySprite(slot, s);
    }

    void HideCharacter(string id, string nodeId)
    {
        int slot = FindSlot(id);
        if (slot < 0) { Warn($"'{id}' não está em cena", nodeId); return; }
        ClearSlot(slot);
    }

    int FindSlot(string id)
    {
        for (int i = 0; i < slotCharId.Length; i++)
            if (string.Equals(slotCharId[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    Sprite FindExpression(string charId, string expr)
    {
        if (!artMap.TryGetValue(charId, out var art)) return null;
        foreach (var e in art.expressions)
            if (e != null && e.sprite != null && e.name != null &&
                string.Equals(e.name.Trim(), expr, StringComparison.OrdinalIgnoreCase)) return e.sprite;
        return null;
    }

    void ApplySprite(int slot, Sprite s)
    {
        var img = slotImages[slot];
        img.enabled = true;
        img.sprite = s;
        float aspect = s.rect.width / s.rect.height;
        img.rectTransform.sizeDelta = new Vector2(characterHeight * aspect, characterHeight);
    }

    void ClearSlot(int slot)
    {
        slotCharId[slot] = null;
        slotImages[slot].sprite = null;
        slotImages[slot].enabled = false;
    }

    void ApplyDim(string speakerId)
    {
        bool speakerVisible = speakerId != null && FindSlot(speakerId) >= 0;
        for (int i = 0; i < slotImages.Length; i++)
        {
            if (slotCharId[i] == null) continue;
            bool dim = dimInactiveCharacters && speakerVisible &&
                       !string.Equals(slotCharId[i], speakerId, StringComparison.OrdinalIgnoreCase);
            slotImages[i].color = dim ? new Color(0.55f, 0.55f, 0.55f, 1f) : Color.white;
        }
    }

    void Warn(string msg, string nodeId) => Debug.LogWarning($"GraphicNovelPlayer [nó {nodeId}]: {msg}", this);

    // =======================================================================
    //  Construção da UI (tudo por script)
    // =======================================================================
    void BuildUI()
    {
        var canvasGO = new GameObject("GraphicNovelCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGO.transform.SetParent(transform, false);
        var canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGO.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        var root = canvasGO.transform;

        if (EventSystem.current == null)
        {
            var es = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
        }

        // Fundo
        bgImage = NewImage("Fundo", root);
        Fill(bgImage.rectTransform);
        bgImage.color = Color.black;
        bgImage.raycastTarget = false;

        // Slots das personagens (ancorados em baixo, por trás da caixa de diálogo)
        for (int i = 0; i < 3; i++)
        {
            var img = NewImage("Personagem_" + SlotNames[i], root);
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(SlotX[i], 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = Vector2.zero;
            img.raycastTarget = false;
            img.enabled = false;
            slotImages[i] = img;
        }

        // Painel das respostas (por cima da caixa de diálogo)
        answersPanel = NewRect("Respostas", root);
        Stretch(answersPanel, new Vector2(0.15f, 0.35f), new Vector2(0.85f, 0.85f), Vector2.zero, Vector2.zero);
        var vlg = answersPanel.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.childAlignment = TextAnchor.LowerCenter;
        vlg.spacing = 14f;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        // Caixa de diálogo
        var panel = NewImage("CaixaDialogo", root);
        Stretch(panel.rectTransform, new Vector2(0.08f, 0.03f), new Vector2(0.92f, 0.32f), Vector2.zero, Vector2.zero);
        StyleImage(panel, dialogueBoxSprite, new Color(0f, 0f, 0f, 0.78f));
        var panelBtn = panel.gameObject.AddComponent<Button>();
        panelBtn.transition = Selectable.Transition.None;
        panelBtn.onClick.AddListener(OnContinue);

        // Caixa do nome (fundo opcional) + texto do nome
        var nameBox = NewImage("CaixaNome", panel.transform);
        var nbRt = nameBox.rectTransform;
        nbRt.anchorMin = nbRt.anchorMax = new Vector2(0f, 1f);
        nbRt.pivot = new Vector2(0f, 1f);
        nbRt.anchoredPosition = new Vector2(30f, -16f);
        nbRt.sizeDelta = new Vector2(480f, 80f);
        StyleImage(nameBox, nameBoxSprite, new Color(0f, 0f, 0f, 0f));
        nameBox.raycastTarget = false;

        nameText = NewText("Nome", nameBox.transform, nameFontSize, TextAlignmentOptions.MidlineLeft);
        nameText.fontStyle = FontStyles.Bold;
        nameText.raycastTarget = false;
        Stretch(nameText.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 0f), new Vector2(-16f, 0f));

        // Texto da fala
        bodyText = NewText("Texto", panel.transform, textFontSize, TextAlignmentOptions.TopLeft);
        bodyText.raycastTarget = false;
        bodyText.enableAutoSizing = true;
        bodyText.fontSizeMin = 24;
        bodyText.fontSizeMax = textFontSize;
        Stretch(bodyText.rectTransform, Vector2.zero, Vector2.one, new Vector2(44f, 76f), new Vector2(-44f, -104f));

        // Botão continuar (>>>)
        continueButton = NewButton("Continuar", panel.transform, continueButtonSprite,
                                   new Color(0.93f, 0.93f, 0.93f, 1f), ">>>",
                                   continueButtonSprite != null ? textColor : new Color(0.2f, 0.2f, 0.2f, 1f), 30, false);
        var cbRt = (RectTransform)continueButton.transform;
        cbRt.anchorMin = cbRt.anchorMax = new Vector2(1f, 0f);
        cbRt.pivot = new Vector2(1f, 0f);
        cbRt.anchoredPosition = new Vector2(-30f, 20f);
        cbRt.sizeDelta = new Vector2(170f, 56f);
        continueButton.onClick.AddListener(OnContinue);
    }

    void AddAnswerButton(string label, UnityAction onClick)
    {
        var b = NewButton("Opcao", answersPanel, answerButtonSprite, new Color(0.12f, 0.12f, 0.14f, 0.92f),
                          label, textColor, textFontSize, true);
        var le = b.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 90f;
        le.preferredHeight = 90f;
        b.onClick.AddListener(onClick);
    }

    void ClearAnswers()
    {
        if (answersPanel == null) return;
        for (int i = answersPanel.childCount - 1; i >= 0; i--)
        {
            var child = answersPanel.GetChild(i).gameObject;
            child.SetActive(false); // para o layout deixar de o contar já neste frame
            Destroy(child);
        }
    }

    // --- helpers de UI ---
    RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    Image NewImage(string name, Transform parent)
    {
        var rt = NewRect(name, parent);
        return rt.gameObject.AddComponent<Image>();
    }

    TextMeshProUGUI NewText(string name, Transform parent, int size, TextAlignmentOptions align)
    {
        var rt = NewRect(name, parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.fontSize = size;
        t.color = textColor;
        t.alignment = align;
        return t;
    }

    Button NewButton(string name, Transform parent, Sprite sprite, Color fallback, string label,
                     Color labelColor, int fontSize, bool autoSize)
    {
        var img = NewImage(name, parent);
        StyleImage(img, sprite, fallback);
        var btn = img.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;

        var t = NewText("Label", img.transform, fontSize, TextAlignmentOptions.Center);
        t.text = label;
        t.color = labelColor;
        t.raycastTarget = false;
        if (autoSize) { t.enableAutoSizing = true; t.fontSizeMin = 24; t.fontSizeMax = fontSize; }
        Stretch(t.rectTransform, Vector2.zero, Vector2.one, new Vector2(24f, 6f), new Vector2(-24f, -6f));
        return btn;
    }

    static void StyleImage(Image img, Sprite s, Color fallback)
    {
        img.sprite = s;
        img.type = (s != null && s.border.sqrMagnitude > 0f) ? Image.Type.Sliced : Image.Type.Simple;
        img.color = s != null ? Color.white : fallback;
    }

    static void Stretch(RectTransform rt, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax)
    {
        rt.anchorMin = min; rt.anchorMax = max;
        rt.offsetMin = offMin; rt.offsetMax = offMax;
    }

    static void Fill(RectTransform rt) => Stretch(rt, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

    // =======================================================================
    //  Ferramenta de editor: preenche as listas a partir do JSON
    //  (menu "⋮" do componente, fora do Play). Só acrescenta, nunca apaga
    //  nem mexe nos sprites que já arrastaste.
    // =======================================================================
    [ContextMenu("Preencher a partir do JSON")]
    void FillFromJson()
    {
        if (storyJson == null) { Debug.LogWarning("Arrasta primeiro o Story Json.", this); return; }
        var s = JsonUtility.FromJson<GNStory>(storyJson.text);
        if (s == null || s.nodes == null) { Debug.LogWarning("JSON inválido.", this); return; }

#if UNITY_EDITOR
        Undo.RecordObject(this, "Preencher a partir do JSON");
#endif
        if (s.characters != null) foreach (var c in s.characters) GetOrAddChar(c.id);
        foreach (var n in s.nodes)
        {
            ScanEvents(n.events);
            if (n.options != null) foreach (var o in n.options) ScanEvents(o.events);
        }
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
        Debug.Log($"Preenchido: {backgrounds.Count} fundos, {characters.Count} personagens.", this);
    }

    void ScanEvents(List<string> evs)
    {
        if (evs == null) return;
        foreach (var raw in evs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var p = raw.Split(':');
            switch (p[0].Trim().ToLowerInvariant())
            {
                case "fundo": if (p.Length >= 2) AddBackground(p[1].Trim()); break;
                case "mostrar": if (p.Length >= 4) AddExpression(p[1].Trim(), p[3].Trim()); break;
                case "expr": if (p.Length >= 3) AddExpression(p[1].Trim(), p[2].Trim()); break;
            }
        }
    }

    void AddBackground(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        foreach (var b in backgrounds)
            if (b != null && string.Equals(b.name, name, StringComparison.OrdinalIgnoreCase)) return;
        backgrounds.Add(new NamedSprite { name = name });
    }

    CharacterArt GetOrAddChar(string id)
    {
        foreach (var c in characters)
            if (c != null && string.Equals(c.id, id, StringComparison.OrdinalIgnoreCase)) return c;
        var n = new CharacterArt { id = id };
        characters.Add(n);
        return n;
    }

    void AddExpression(string charId, string expr)
    {
        if (string.IsNullOrEmpty(charId) || string.IsNullOrEmpty(expr)) return;
        var c = GetOrAddChar(charId);
        foreach (var e in c.expressions)
            if (e != null && string.Equals(e.name, expr, StringComparison.OrdinalIgnoreCase)) return;
        c.expressions.Add(new NamedSprite { name = expr });
    }
}