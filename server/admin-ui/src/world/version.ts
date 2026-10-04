// How versions are shown across the pages: stage, version, build.
// Bump PRODUCT_VERSION together with the add-on's plugin.cfg, kAddonVersion in
// yhde_session.cpp and the server's ServerInfo.Version.
export const STAGE = "Beta"
export const PRODUCT_VERSION = "0.6.0"
export const BUILD = __BUILD__

export const versionLabel = (v: string = PRODUCT_VERSION) => `${STAGE} ${v.replace(/^v/, "")}`
export const versionFull = (v: string = PRODUCT_VERSION) => `${versionLabel(v)} · build ${BUILD}`
