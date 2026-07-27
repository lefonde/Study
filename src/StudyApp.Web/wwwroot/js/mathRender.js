// Renders LaTeX within an already-rendered markdown element via KaTeX auto-render
// (loaded globally in App.razor). Users write $...$ / $$...$$ in card/notes markdown;
// Markdig's math extension (part of UseAdvancedExtensions) converts that to literal
// \(...\) / \[...\] text wrapped in <span class="math">/<div class="math"> *before*
// this ever runs — so auto-render must target \(...\)/\[...\], not the original $.
export function renderMath(element) {
    if (!element || typeof window.renderMathInElement !== "function")
        return;

    window.renderMathInElement(element, {
        delimiters: [
            { left: "\\[", right: "\\]", display: true },
            { left: "\\(", right: "\\)", display: false },
        ],
        throwOnError: false,
    });
}
