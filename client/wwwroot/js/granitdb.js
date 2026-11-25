// Scripts de l'application, hors de index.html pour que la Content-Security-Policy n'ait pas besoin de 'unsafe-inline'.
// Service worker : une nouvelle version prend la main d'elle-même ; la page propose alors de recharger (rien n'est rechargé sans l'accord de l'utilisateur).
if ("serviceWorker" in navigator) {
	const hadController = !!navigator.serviceWorker.controller;
	navigator.serviceWorker.register("service-worker.js");
	navigator.serviceWorker.addEventListener("controllerchange", () => {
		if (!hadController || document.getElementById("update-banner")) return;
		const bar = document.createElement("div");
		bar.id = "update-banner";
		bar.setAttribute("role", "status");
		bar.style.cssText = "position:fixed;left:50%;bottom:1rem;transform:translateX(-50%);z-index:2000;display:flex;gap:1rem;align-items:center;padding:.6rem 1rem;border-radius:6px;background:#1e293b;color:#fff;box-shadow:0 2px 8px rgba(0,0,0,.4);font:inherit";
		bar.textContent = "Une nouvelle version de GranitDb est disponible.";
		const button = document.createElement("button");
		button.type = "button";
		button.textContent = "Recharger";
		button.style.cssText = "font:inherit;padding:.25rem .75rem;border:1px solid #fff;border-radius:4px;background:#fff;color:#1e293b;cursor:pointer";
		button.onclick = () => location.reload();
		bar.appendChild(button);
		document.body.appendChild(bar);
	});
}
	
// Fait défiler jusqu'à l'élément d'identifiant donné (table ouverte depuis un résultat de recherche).
window.granitdb = { scrollToId: id => document.getElementById(id)?.scrollIntoView({ behavior: "smooth", block: "start" }) };
	
// Exports du diagramme : téléchargement d'un texte, d'un SVG converti en PNG, copie dans le presse-papiers.
function granitdbSave(fileName, blob) {
	const url = URL.createObjectURL(blob);
	const a = document.createElement("a");
	a.href = url;
	a.download = fileName;
	document.body.appendChild(a);
	a.click();
	a.remove();
	setTimeout(() => URL.revokeObjectURL(url), 1000);
}
window.granitdb.download = (fileName, mime, content) => granitdbSave(fileName, new Blob([content], { type: mime + ";charset=utf-8" }));
// Fichier binaire (xlsx) reçu en base64.
window.granitdb.downloadBase64 = (fileName, mime, base64) => {
	const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
	granitdbSave(fileName, new Blob([bytes], { type: mime }));
};
// Ouvre la documentation HTML dans un onglet, directement depuis l'API (pas de blob : un document blob hériterait de la
// Content-Security-Policy de l'application et ses scripts intégrés seraient bloqués).
function granitdbOpenTab(url) {
	const opened = window.open(url, "_blank");
	if (!opened) throw new Error("Le navigateur a bloqué l'ouverture de l'onglet : autorisez les fenêtres pour ce site.");
}
// Version imprimable (l'utilisateur enregistre en PDF depuis l'impression).
window.granitdb.openPrintable = url => granitdbOpenTab(url + "#/print");
window.granitdb.openHtml = url => granitdbOpenTab(url);
window.granitdb.copyText = text => navigator.clipboard.writeText(text);
// Domaine sous le point (x, y) compté depuis le coin de l'élément : cible d'une table lâchée hors du diagramme.
window.granitdb.domainAt = (element, x, y) => {
	const box = element.getBoundingClientRect();
	const target = document.elementFromPoint(box.left + x, box.top + y);
	return target?.closest("[data-domain-id]")?.getAttribute("data-domain-id") ?? null;
};
window.granitdb.downloadPng = (fileName, svg, width, height) => new Promise((resolve, reject) => {
	const scale = 2;
	const image = new Image();
	image.onload = () => {
		const canvas = document.createElement("canvas");
		canvas.width = Math.ceil(width * scale);
		canvas.height = Math.ceil(height * scale);
		const context = canvas.getContext("2d");
		context.fillStyle = "#ffffff";
		context.fillRect(0, 0, canvas.width, canvas.height);
		context.drawImage(image, 0, 0, canvas.width, canvas.height);
		canvas.toBlob(blob => {
			if (!blob) { reject("PNG impossible"); return; }
			granitdbSave(fileName, blob);
			resolve();
		}, "image/png");
	};
	image.onerror = () => reject("SVG illisible");
	image.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
});
	
// Raccourcis clavier de l'espace de travail et déplacement dans l'arbre de l'explorateur.
(function () {
	let handler = null;
	const typing = el => el && (el.tagName === "INPUT" || el.tagName === "TEXTAREA" || el.isContentEditable);
	window.granitdb.registerShortcuts = ref => {
		window.granitdb.unregisterShortcuts();
		handler = e => {
			const key = e.key.toLowerCase();
			if ((e.ctrlKey || e.metaKey) && key === "k") { e.preventDefault(); ref.invokeMethodAsync("OnShortcut", "palette"); }
			else if (e.key === "?" && !e.ctrlKey && !e.metaKey && !typing(e.target)) { e.preventDefault(); ref.invokeMethodAsync("OnShortcut", "shortcuts"); }
			else if (e.altKey && key === "w") { e.preventDefault(); ref.invokeMethodAsync("OnShortcut", "closeTab"); }
			else if ((e.ctrlKey || e.metaKey) && key === "b" && !typing(e.target)) { e.preventDefault(); ref.invokeMethodAsync("OnShortcut", "toggleExplorer"); }
			else if (["ArrowUp", "ArrowDown", "Home", "End"].includes(e.key) && e.target.closest && e.target.closest(".explorer [role=treeitem]")) { e.preventDefault(); }
		};
		document.addEventListener("keydown", handler);
	};
	window.granitdb.unregisterShortcuts = () => { if (handler) document.removeEventListener("keydown", handler); handler = null; };
	window.granitdb.isTyping = () => typing(document.activeElement);
	window.granitdb.scrollIntoView = selector => { const el = document.querySelector(selector); if (el) el.scrollIntoView({ block: 'nearest' }); };
	window.granitdb.focus = selector => { const el = document.querySelector(selector); if (el) el.focus(); };
	// Grille de saisie : place le focus dans la cellule (ligne, colonne) de la grille.
	window.granitdb.focusCell = (gridId, row, col) => { const el = document.querySelector('#' + gridId + ' [data-cell="' + row + ':' + col + '"]'); if (el) el.focus(); };
	// where : next, prev, first, last ou parent (par rapport à l'élément de l'arbre qui a le focus).
	window.granitdb.moveTreeFocus = where => {
		const items = Array.from(document.querySelectorAll(".explorer [role=treeitem]")).filter(el => el.offsetParent !== null);
		const current = document.activeElement && document.activeElement.closest ? document.activeElement.closest("[role=treeitem]") : null;
		const index = items.indexOf(current);
		let target = null;
		if (where === "first") target = items[0];
		else if (where === "last") target = items[items.length - 1];
		else if (where === "next") target = items[Math.min(items.length - 1, index + 1)];
		else if (where === "prev") target = items[Math.max(0, index - 1)];
		else if (where === "parent" && current) {
			const level = parseInt(current.getAttribute("aria-level") || "1", 10);
			for (let i = index - 1; i >= 0; i--) if (parseInt(items[i].getAttribute("aria-level") || "1", 10) < level) { target = items[i]; break; }
		}
		if (target) target.focus();
	};
})();
	
