// Boutons de telechargement : pointent sur l'installeur de la derniere release GitHub.
// Sans reseau ou si l'API refuse (60 requetes/heure), ils gardent le lien vers la page de la release.
(function () {
  const REPO = "Skynizz/Prism";

  fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { headers: { Accept: "application/vnd.github+json" } })
    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
    .then(rel => {
      const find = re => rel.assets.find(a => re.test(a.name));
      const setup = find(/^Prism-Setup-.*\.exe$/i);
      const zip = find(/^Prism-.*-win-x64\.zip$/i);
      const sha = find(/^Prism-Setup-.*\.exe\.sha256$/i);
      const version = (rel.tag_name || "").replace(/^v/, "");
      if (setup) {
        document.querySelectorAll("[data-download]").forEach(a => (a.href = setup.browser_download_url));
        const mb = (setup.size / 1048576).toFixed(0);
        const when = new Date(rel.published_at).toLocaleDateString("en", { month: "long", day: "numeric", year: "numeric" });
        const meta = document.querySelector("[data-meta]");
        if (meta) meta.textContent = `Version ${version} · ${mb} MB · ${when} · Windows 10 & 11 x64`;
      }
      if (zip) document.querySelectorAll("[data-zip]").forEach(a => (a.href = zip.browser_download_url));
      if (sha) document.querySelectorAll("[data-sha]").forEach(a => (a.href = sha.browser_download_url));
      if (version) document.querySelectorAll("[data-version]").forEach(s => (s.textContent = version));
    })
    .catch(() => {});

  // Hors Windows : on le dit, sans cacher le bouton.
  if (!/Windows/i.test(navigator.userAgent)) {
    const note = document.querySelector("[data-os-note]");
    if (note) note.hidden = false;
  }

  // Apparition douce des sections au defilement.
  const targets = document.querySelectorAll(".row, .card, .wide, .pillars li, .section-head, .frame, details");
  if ("IntersectionObserver" in window) {
    const io = new IntersectionObserver(entries => {
      entries.forEach(e => { if (e.isIntersecting) { e.target.classList.add("on"); io.unobserve(e.target); } });
    }, { rootMargin: "0px 0px -8% 0px" });
    targets.forEach(t => { t.classList.add("reveal"); io.observe(t); });
  }
})();
