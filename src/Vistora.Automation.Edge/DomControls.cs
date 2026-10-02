using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;
using Microsoft.Playwright;
using Vistora.Core;

namespace Vistora.Automation.Edge;

// Toda leitura e interação ocorre no documento exibido pelo navegador.
internal sealed class DomControls(IPage page, IReadOnlyDictionary<string, string> overrides, DiagnosticAttempt? diagnostics = null)
{
    private readonly Dictionary<string, int> candidateCounts = [];
    private void Lookup(string key, string strategy, int count) => diagnostics?.Event("control.lookup", "Controle procurado.",
        DiagnosticLevel.Debug, new() { ["controlKey"] = key, ["strategy"] = strategy, ["count"] = count });
    public static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    internal static string NormalizePersonName(string name) => new(Normalize(name).Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    // O Jira acrescenta texto para leitores de tela ao nome acessível dos campos obrigatórios.
    internal static Regex LabelPattern(string label) => new($"^{Regex.Escape(label).Replace("/", @"\/")}\\s*[:*]?\\s*(?:\\((?:required|obrigat[óo]rio)\\))?\\s*$", RegexOptions.IgnoreCase);

    public async Task<ILocator> FieldAsync(string key, string[] names, ILocator? scope = null)
    {
        var field = await TryFieldAsync(key, names, scope);
        if (field is not null) return field;
        var count = candidateCounts.GetValueOrDefault(key);
        var code = count > 1 ? "FIELD_AMBIGUOUS" : "FIELD_NOT_FOUND";
        diagnostics?.Event("control.missing", "Campo não identificado de forma única.", DiagnosticLevel.Warning,
            new() { ["controlKey"] = key, ["count"] = count }, errorCode: code);
        var error = new InvalidOperationException($"Não foi possível identificar o campo “{names[0]}”. Confira a página do Jira e o diagnóstico da execução.");
        error.Data["DiagnosticCode"] = code; throw error;
    }

    public async Task<ILocator?> TryFieldAsync(string key, string[] names, ILocator? scope = null)
    {
        candidateCounts[key] = 0;
        if (overrides.TryGetValue(key, out var selector))
        {
            var configured = (scope ?? page.Locator("body")).Locator(selector).Filter(new() { Visible = true });
            var count = await configured.CountAsync(); candidateCounts[key] = count; Lookup(key, "override", count);
            return await IsUniqueVisibleAsync(configured) ? configured : null;
        }
        foreach (var name in names)
        {
            var labelled = (scope is null ? page.GetByLabel(LabelPattern(name)) : scope.GetByLabel(LabelPattern(name))).Filter(new() { Visible = true });
            var count = await labelled.CountAsync(); candidateCounts[key] = Math.Max(candidateCounts[key], count); Lookup(key, "label", count);
            if (await IsUniqueVisibleAsync(labelled)) return labelled;
            var textbox = scope is null ? page.GetByRole(AriaRole.Textbox, new() { NameRegex = LabelPattern(name) })
                : scope.GetByRole(AriaRole.Textbox, new() { NameRegex = LabelPattern(name) });
            Lookup(key, "role", await textbox.CountAsync());
            if (await IsUniqueVisibleAsync(textbox)) return textbox;
            var label = (scope is null ? page.GetByText(LabelPattern(name)) : scope.GetByText(LabelPattern(name))).Filter(new() { Visible = true });
            if (await label.CountAsync() != 1) continue;
            var ancestor = label;
            for (var depth = 0; depth < 4; depth++)
            {
                ancestor = ancestor.Locator("..");
                var controls = ancestor.Locator("input:not([type=hidden]), textarea, select, [contenteditable=true], [role=combobox]").Filter(new() { Visible = true });
                // Um combobox pode conter seu próprio input; preferir o controle editável.
                var inputs = controls.Filter(new() { HasNot = page.Locator("input,textarea,[contenteditable=true]") });
                Lookup(key, "ancestor", await inputs.CountAsync());
                if (await IsUniqueVisibleAsync(inputs)) return inputs;
                if (await IsUniqueVisibleAsync(controls)) return controls;
            }
        }
        return null;
    }

    public async Task FillAsync(string key, string[] labels, string value, ILocator? scope = null)
    {
        var field = await FieldAsync(key, labels, scope);
        await field.FillAsync(value);
        // Sair do campo dispara a validação do Jira; aguardar evita avançar com o estado anterior.
        await field.PressAsync("Tab");
        await Task.Delay(400);
        var tag = await field.EvaluateAsync<string>("e => e.tagName.toLowerCase()");
        var actual = tag is "input" or "textarea" ? await field.InputValueAsync() : await field.InnerTextAsync();
        var matches = Normalize(actual) == Normalize(value);
        diagnostics?.Event("control.filled", "Preenchimento conferido.", matches ? DiagnosticLevel.Debug : DiagnosticLevel.Warning,
            new() { ["controlKey"] = key, ["matches"] = matches });
        if (!matches) throw new InvalidOperationException($"O preenchimento de “{labels[0]}” não foi confirmado.");
    }

    public async Task ChooseAsync(string key, string[] labels, string query, Regex exactOption, string expected, ILocator? scope = null)
    {
        var field = await FieldAsync(key, labels, scope);
        var tag = await field.EvaluateAsync<string>("e => e.tagName.toLowerCase()");
        if (tag == "select")
        {
            await field.SelectOptionAsync(new SelectOptionValue { Label = expected });
            diagnostics?.Event("control.selected", "Opção selecionada.", DiagnosticLevel.Debug, new() { ["controlKey"] = key });
            return;
        }
        // Os comboboxes do Jira cobrem o input com o valor selecionado. Focar o input
        // permite pesquisar sem clicar no texto que intercepta eventos do ponteiro.
        if (tag is "input" or "textarea") { await field.FocusAsync(); await field.FillAsync(query); }
        else
        {
            await field.ClickAsync();
            var input = field.Locator("input");
            if (await input.CountAsync() == 1) await input.FillAsync(query);
            else { await page.Keyboard.PressAsync("Control+A"); await page.Keyboard.InsertTextAsync(query); }
        }
        var option = page.GetByRole(AriaRole.Option, new() { NameRegex = exactOption });
        if (await option.CountAsync() == 0) option = page.GetByText(exactOption).Filter(new() { Visible = true });
        await option.First.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        option = await UniqueAsync(option, expected);
        await option.ClickAsync();
        diagnostics?.Event("control.selected", "Opção selecionada.", DiagnosticLevel.Debug, new() { ["controlKey"] = key });
        var selectionDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        do
        {
            if (await field.GetAttributeAsync("aria-expanded") == "true") { await Task.Delay(100); continue; }
            var actual = await field.EvaluateAsync<string>("""
                e => {
                  const selected = (e.getAttribute('aria-describedby') || '').split(/\s+/)
                    .filter(id => id.endsWith('-single-value')).map(id => document.getElementById(id)?.textContent || '').join(' ');
                  const container = e.closest('[class~="-ValueContainer"]');
                  return selected || container?.innerText || e.value || e.innerText || e.textContent || '';
                }
                """);
            if (Normalize(actual).Contains(Normalize(expected), StringComparison.OrdinalIgnoreCase)) return;
            var group = await field.Locator("..").InnerTextAsync();
            if (Normalize(group).Contains(Normalize(expected), StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(100);
        } while (DateTimeOffset.UtcNow < selectionDeadline);
        throw new InvalidOperationException($"A seleção de “{labels[0]}” não foi confirmada.");
    }

    public async Task<string> ValueAsync(string key, string[] names)
    {
        var value = await TryValueAsync(key, names);
        if (value is not null) return value;
        diagnostics?.Event("control.value_missing", "Valor não encontrado para conferência.", DiagnosticLevel.Warning,
            new() { ["controlKey"] = key }, errorCode: "FIELD_NOT_FOUND");
        throw new InvalidOperationException($"Não foi possível conferir “{names[0]}” na página do chamado.");
    }

    public async Task<string?> TryValueAsync(string key, string[] names)
    {
        if (overrides.TryGetValue(key, out var selector)) return Normalize(await (await UniqueAsync(page.Locator(selector), names[0])).InnerTextAsync());
        foreach (var name in names)
        {
            var label = page.GetByText(LabelPattern(name)).Filter(new() { Visible = true });
            if (await label.CountAsync() != 1) continue;
            var value = await label.EvaluateAsync<string>("""
                e => {
                  const label = (e.innerText || e.textContent || '').trim();
                  let node = e;
                  for (let i = 0; i < 5 && node; i++, node = node.parentElement) {
                    if (node.nextElementSibling) {
                      const t = (node.nextElementSibling.innerText || '').trim();
                      if (t) return t;
                    }
                    const t = (node.innerText || '').trim();
                    if (t.startsWith(label) && t.length > label.length) return t.slice(label.length).trim();
                  }
                  return '';
                }
                """);
            if (!string.IsNullOrWhiteSpace(value)) return Normalize(value);
        }
        return null;
    }

    public async Task ClickAsync(string key, string[] names, ILocator? scope = null)
    {
        if (overrides.TryGetValue(key, out var selector))
        {
            await (await UniqueAsync((scope ?? page.Locator("body")).Locator(selector), names[0])).ClickAsync();
            diagnostics?.Event("action.clicked", "Clique executado; o resultado ainda requer conferência.", details: new() { ["controlKey"] = key, ["strategy"] = "override" }, outcome: "clicked");
            return;
        }
        foreach (var name in names)
        {
            foreach (var role in new[] { AriaRole.Button, AriaRole.Link, AriaRole.Menuitem })
            {
                var locator = scope is null ? page.GetByRole(role, new() { Name = name, Exact = true }) : scope.GetByRole(role, new() { Name = name, Exact = true });
                var count = await locator.CountAsync(); Lookup(key, role.ToString(), count);
                if (await IsUniqueVisibleAsync(locator))
                { await locator.ClickAsync(); diagnostics?.Event("action.clicked", "Clique executado; o resultado ainda requer conferência.", details: new() { ["controlKey"] = key }, outcome: "clicked"); return; }
            }
            var text = (scope is null ? page.GetByText(name, new() { Exact = true }) : scope.GetByText(name, new() { Exact = true })).Filter(new() { Visible = true });
            if (await IsUniqueVisibleAsync(text))
            { await text.ClickAsync(); diagnostics?.Event("action.clicked", "Clique executado; o resultado ainda requer conferência.", details: new() { ["controlKey"] = key }, outcome: "clicked"); return; }
        }
        diagnostics?.Event("control.action_missing", "Ação indisponível na página.", DiagnosticLevel.Warning, new() { ["controlKey"] = key }, errorCode: "FIELD_NOT_FOUND");
        throw new InvalidOperationException($"A ação “{names[0]}” não está disponível na página atual.");
    }

    public static async Task<bool> IsUniqueVisibleAsync(ILocator locator) => await locator.CountAsync() == 1 && await locator.IsVisibleAsync();
    public static async Task<ILocator> UniqueAsync(ILocator locator, string name)
    {
        if (!await IsUniqueVisibleAsync(locator)) throw new InvalidOperationException($"A página não oferece uma opção única para “{name}”. A execução foi interrompida para conferência.");
        return locator;
    }
}
