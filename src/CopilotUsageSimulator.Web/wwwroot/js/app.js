window.simulator = {
    downloadText: (fileName, content) => {
        const blob = new Blob([content], { type: "application/json" });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement("a");
        anchor.href = url;
        anchor.download = fileName;
        anchor.click();
        URL.revokeObjectURL(url);
    }
};

window.costCompass = {
    getTheme: () => document.documentElement.getAttribute("data-theme") || "light",
    toggleTheme: () => {
        const theme = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
        document.documentElement.setAttribute("data-theme", theme);
        const url = new URL(window.location.href);
        url.searchParams.set("scoutTheme", theme);
        window.history.replaceState(window.history.state, "", url);
        return theme;
    }
};
