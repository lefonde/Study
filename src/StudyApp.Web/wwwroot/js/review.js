let handler = null;

export function init(dotnet) {
    dispose();
    handler = (e) => {
        if (e.repeat || (e.target instanceof Element && e.target.matches("input, textarea, select")))
            return;
        if (e.key === " ") {
            e.preventDefault();
            dotnet.invokeMethodAsync("OnKey", "space");
        } else if (["1", "2", "3", "4"].includes(e.key)) {
            dotnet.invokeMethodAsync("OnKey", e.key);
        }
    };
    document.addEventListener("keydown", handler);
}

export function dispose() {
    if (handler) {
        document.removeEventListener("keydown", handler);
        handler = null;
    }
}
