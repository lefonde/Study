// Draws the connector lines of the course progression map.
//
// The DOM is the whole payload: every node carries data-topic="{id}" and, when it depends on
// something, data-requires="{id},{id}". Nothing about the graph crosses the interop boundary —
// Blazor renders the nodes, this measures where they landed and joins them up.
//
// Lines have to be drawn rather than laid out because their endpoints are only knowable after
// the browser has wrapped and sized the stage columns, which depends on the viewport and on how
// long the topic names are.

const OBSERVERS = new WeakMap();

function draw(container) {
    const svg = container.querySelector("[data-edges]");
    if (!svg) return;

    const box = container.getBoundingClientRect();
    svg.setAttribute("viewBox", `0 0 ${box.width} ${box.height}`);
    svg.setAttribute("width", box.width);
    svg.setAttribute("height", box.height);

    const nodes = new Map();
    for (const el of container.querySelectorAll("[data-topic]"))
        nodes.set(el.dataset.topic, el.getBoundingClientRect());

    const paths = [];
    for (const el of container.querySelectorAll("[data-requires]")) {
        const to = nodes.get(el.dataset.topic);
        if (!to) continue;

        for (const requiredId of el.dataset.requires.split(",").filter(Boolean)) {
            const from = nodes.get(requiredId);
            if (!from) continue;

            // Leave the prerequisite on its right edge and arrive on the dependent's left, which
            // is what makes the flow read left-to-right even when a stage has wrapped.
            const x1 = from.right - box.left;
            const y1 = from.top + from.height / 2 - box.top;
            const x2 = to.left - box.left;
            const y2 = to.top + to.height / 2 - box.top;

            // Horizontal control points: the curve leaves and enters sideways, so a link that
            // spans several stages doesn't cut diagonally across the cards in between.
            const reach = Math.max(24, Math.abs(x2 - x1) * 0.4);
            paths.push(
                `<path d="M ${x1} ${y1} C ${x1 + reach} ${y1}, ${x2 - reach} ${y2}, ${x2} ${y2}" ` +
                `fill="none" stroke="currentColor" stroke-width="1.5" opacity="0.45" ` +
                `marker-end="url(#graph-arrow)" />`);
        }
    }

    svg.innerHTML =
        `<defs><marker id="graph-arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="6"` +
        ` markerHeight="6" orient="auto-start-reverse">` +
        `<path d="M 0 0 L 8 4 L 0 8 z" fill="currentColor" opacity="0.55" /></marker></defs>` +
        paths.join("");
}

export function drawEdges(container) {
    if (!container) return;

    draw(container);

    if (!OBSERVERS.has(container)) {
        // Re-draw on reflow: a resized window, a wrapped column or a font finishing loading all
        // move the nodes, and lines pointing at where a card used to be are worse than none.
        const observer = new ResizeObserver(() => draw(container));
        observer.observe(container);
        OBSERVERS.set(container, observer);
    }
}

export function disposeEdges(container) {
    const observer = container && OBSERVERS.get(container);
    if (observer) {
        observer.disconnect();
        OBSERVERS.delete(container);
    }
}
