// Who runs YHDE. The business ID (Y-tunnus) and street address are still to
// be filled in: EU law requires them on the site once YHDE sells anything.
// Empty fields are left off the pages.
export const COMPANY = {
  name: "YHDE Development Organization",
  businessId: "",
  address: "",
  country: "Finland",
  email: "contact@frostinteractive.fi",
  support: "support@frostinteractive.fi",
  billing: "billing@frostinteractive.fi",
  privacy: "privacy@frostinteractive.fi",
  info: "info@frostinteractive.fi",
}

export const LEGAL_UPDATED = "27 September 2026"

// The company in one line: name, business ID and address, as far as known.
export const companyLine = () =>
  [COMPANY.name, COMPANY.businessId && `business ID ${COMPANY.businessId}`, COMPANY.address || COMPANY.country].filter(Boolean).join(", ")

// YHDE's community links, shown in the footer and on the contact page. One
// without an address yet shows as "coming soon" and can't be clicked.
export const SOCIAL = {
  discord: "",
  github: "https://github.com/YhdeDevelopmentOrganization/yhde-godot",
  youtube: "",
  x: "",
}
