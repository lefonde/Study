// Draws the connector lines of the course progression map, and handles focusing one topic.
//
// The DOM is the whole payload: a card carries data-topic="{id}" and, when it depends on something,
// data-requires="{id},{id}"; a reserved lane carries data-lane="{fromId}>{toId}". Nothing about the
// graph crosses the interop boundary — Blazor places everything on a CSS grid, this measures where
// it landed and joins it up.
//
// Lines have to be drawn rather than laid out because their endpoints are only knowable after the
// browser has sized the columns, which depends on the viewport, the zoom and how long the topic
// names are.
//
// Positions come from offsetLeft/offsetTop, never getBoundingClientRect. Two reasons, both learned
// the hard way: offsets are relative to the container (the graph is position: relative, so it is the
// offset parent) and therefore already in the same coordinate space as the absolutely-positioned
// SVG; and they do not change when the graph is scrolled, so a horizontally scrolled graph draws
// correctly with no scroll listener. Measuring with client rects made every line drift by
// scrollLeft the moment the graph was wide enough to scroll — which, past five stages, is always.

const STATE = new WeakMap();

/// Where a link should leave or enter a card, in container coordinates.
function anchors(el) {
    return {
        left: el.offsetLeft,
        right: el.offsetLeft + el.offsetWidth,
        middle: el.offsetTop + el.offsetHeight / 2,
    };
}

/// A cubic through a list of points, entering and leaving each one horizontally.
///
/// Both control points sit on the vertical midline between the two anchors. That is not a taste
/// decision: a bezier is contained in the convex hull of its control points, so putting every
/// control x at the midpoint confines each segment to the bounding box of its own two endpoints.
/// Since consecutive anchors are always separated by a gutter — or by the width of a reserved lane
/// whose row holds no card — the line provably cannot pass through a card.
///
/// The previous heuristic pushed control points out by a share of the vertical drop, which on a
/// tall link reached far enough to bulge into the neighbouring column and disappear behind the
/// cards there.
function curve(points) {
    let d = `M ${points[0].x} ${points[0].y}`;
    for (let i = 1; i < points.length; i++) {
        const from = points[i - 1];
        const to = points[i];
        const mid = (from.x + to.x) / 2;
        d += ` C ${mid} ${from.y}, ${mid} ${to.y}, ${to.x} ${to.y}`;
    }
    return d;
}

function draw(container) {
    const svg = container.querySelector("[data-edges]");
    if (!svg) return;

    // Sized from the cards, not from scrollWidth. It has to cover the whole scroll content —
    // sizing it to the visible box clipped away every line whose card sat past the right edge,
    // since SVG hides its overflow by default. But scrollWidth cannot be the source: the SVG is
    // absolutely positioned inside the scroller, so it contributes to scrollWidth itself, and a
    // size taken from that can only ever grow. Zooming out left it stuck at the old width forever.
    let width = container.clientWidth;
    let height = container.clientHeight;
    for (const el of container.children) {
        if (el === svg) continue;
        width = Math.max(width, el.offsetLeft + el.offsetWidth);
        height = Math.max(height, el.offsetTop + el.offsetHeight);
    }
    svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
    svg.setAttribute("width", width);
    svg.setAttribute("height", height);

    const cards = new Map();
    for (const el of container.querySelectorAll("[data-topic]"))
        cards.set(el.dataset.topic, anchors(el));

    // Lanes held open in the stages a long link passes over, keyed the same way Blazor wrote them.
    const lanes = new Map();
    for (const el of container.querySelectorAll("[data-lane]")) {
        const key = el.dataset.lane;
        if (!lanes.has(key)) lanes.set(key, []);
        lanes.get(key).push(anchors(el));
    }

    const halos = [];
    const lines = [];

    for (const el of container.querySelectorAll("[data-requires]")) {
        const to = cards.get(el.dataset.topic);
        if (!to) continue;

        for (const fromId of (el.dataset.requires || "").split(",").filter(Boolean)) {
            const from = cards.get(fromId);
            if (!from) continue;

            const waypoints = (lanes.get(`${fromId}>${el.dataset.topic}`) || [])
                .slice()
                .sort((a, b) => a.left - b.left);

            const points = [{ x: from.right, y: from.middle }];
            // A lane is entered on its left and left on its right, exactly like a card. That keeps
            // every change of height inside a gutter and makes the traversal of the stage a
            // horizontal run along a row that is guaranteed to hold no card — which is the whole
            // reason the layout reserves the row in the first place.
            for (const lane of waypoints) {
                points.push({ x: lane.left, y: lane.middle });
                points.push({ x: lane.right, y: lane.middle });
            }
            points.push({ x: to.left, y: to.middle });

            const d = curve(points);
            const tag = `data-from="${fromId}" data-to="${el.dataset.topic}"`;

            // A halo in the page colour under every line, so where two do cross one visibly passes
            // over the other instead of the pair merging into a smudge.
            halos.push(`<path d="${d}" fill="none" stroke="var(--bs-body-bg, #fff)" stroke-width="5"
                stroke-linecap="round" opacity="0.9" />`);
            lines.push(`<path class="graph-edge" ${tag} d="${d}" fill="none" stroke="currentColor"
                stroke-width="1.5" opacity="0.5" marker-end="url(#graph-arrow)" />`);
        }
    }

    svg.innerHTML =
        `<defs><marker id="graph-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8"` +
        ` markerHeight="8" orient="auto-start-reverse">` +
        `<path d="M 0 0 L 10 5 L 0 10 z" fill="currentColor" opacity="0.6" /></marker></defs>` +
        // Halos first so every real line paints over them.
        halos.join("") + lines.join("");

    applyFocus(container);
}

/// Repaints the highlight from whatever is currently focused. Kept separate from draw so a pointer
/// move costs a few class changes rather than a full re-measure.
function applyFocus(container) {
    const state = STATE.get(container);
    const focused = state && (state.pinned || state.hovered);

    container.classList.toggle("has-focus", !!focused);

    const near = new Set();
    if (focused) {
        near.add(focused);
        for (const path of container.querySelectorAll(".graph-edge")) {
            if (path.dataset.from === focused) near.add(path.dataset.to);
            if (path.dataset.to === focused) near.add(path.dataset.from);
        }
    }

    for (const el of container.querySelectorAll("[data-topic]")) {
        el.classList.toggle("focus-self", el.dataset.topic === focused);
        el.classList.toggle("focus-near", focused ? near.has(el.dataset.topic) : false);
    }

    for (const path of container.querySelectorAll(".graph-edge")) {
        const own = focused
            && (path.dataset.from === focused || path.dataset.to === focused);
        path.setAttribute("opacity", !focused ? "0.5" : own ? "0.95" : "0.06");
        path.setAttribute("stroke", own ? "#0d6efd" : "currentColor");
        path.setAttribute("stroke-width", own ? "2.5" : "1.5");
    }
}

function bindFocus(container) {
    const state = STATE.get(container);

    container.addEventListener("pointerover", (e) => {
        const card = e.target.closest("[data-topic]");
        const id = card ? card.dataset.topic : null;
        if (state.hovered === id) return;
        state.hovered = id;
        applyFocus(container);
    });

    container.addEventListener("pointerleave", () => {
        state.hovered = null;
        applyFocus(container);
    });

    // Pinning survives the pointer leaving, which is what lets you actually read the cards a link
    // joins. Blazor also listens for this click (isolate mode), and both are wanted.
    container.addEventListener("click", (e) => {
        const card = e.target.closest("[data-topic]");
        const id = card ? card.dataset.topic : null;
        state.pinned = state.pinned === id ? null : id;
        applyFocus(container);
    });
}

export function drawEdges(container) {
    if (!container) return;

    if (!STATE.has(container)) {
        STATE.set(container, { hovered: null, pinned: null, observer: null });
        bindFocus(container);

        // Re-draw on reflow: a resize, a zoom change, a wrapped column or a font finishing loading
        // all move the cards, and a line pointing at where a card used to be is worse than none.
        const observer = new ResizeObserver(() => draw(container));
        observer.observe(container);
        STATE.get(container).observer = observer;
    }

    draw(container);
}

/// Called by Blazor after a re-render that replaces the cards, so a stale highlight cannot survive
/// on a card the pointer has already left.
export function clearFocus(container) {
    const state = container && STATE.get(container);
    if (!state) return;

    state.hovered = null;
    state.pinned = null;
    applyFocus(container);
}

export function disposeEdges(container) {
    const state = container && STATE.get(container);
    if (state?.observer) {
        state.observer.disconnect();
        STATE.delete(container);
    }
}
