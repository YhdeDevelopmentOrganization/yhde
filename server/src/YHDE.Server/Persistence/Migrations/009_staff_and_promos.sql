-- 009: staff accounts on the admin page, and promo codes (admin.md)

-- People who run YHDE. 'admin' may do everything on the admin page;
-- 'support' may look at everything and help people (sign them out, send a
-- new confirmation email) but change nothing else. NULL: a customer.
ALTER TABLE users ADD COLUMN IF NOT EXISTS staff_role TEXT CHECK (staff_role IN ('admin', 'support'));

-- A promo code, drafted on the admin page. Codes are never listed anywhere a
-- customer can see; a team owner types one on the Billing page.
--   kind free_months:  `months` months of the plan free
--   kind percent_off:  `amount` % off for `months` months (NULL: for good)
--   kind amount_off:   `amount` euros off each period for `months` months
--   kind extra_seats:  `amount` more seats for `months` months
--   kind extra_storage:`amount` GB more storage for `months` months
CREATE TABLE IF NOT EXISTS promo_codes (
    code_id          UUID          PRIMARY KEY,
    code             TEXT          NOT NULL UNIQUE,
    note             TEXT          NOT NULL DEFAULT '',
    active           BOOLEAN       NOT NULL DEFAULT TRUE,
    starts_at        TIMESTAMPTZ,
    ends_at          TIMESTAMPTZ,
    plans            TEXT[],
    new_teams_only   BOOLEAN       NOT NULL DEFAULT FALSE,
    kind             TEXT          NOT NULL CHECK (kind IN ('free_months', 'percent_off', 'amount_off', 'extra_seats', 'extra_storage')),
    amount           NUMERIC(10,2) NOT NULL DEFAULT 0,
    months           INT,
    max_redemptions  INT,
    created_by       UUID,
    created_at       TIMESTAMPTZ   NOT NULL DEFAULT NOW()
);

-- Which team used which code, once each; `until` is when the benefit ends
-- (NULL: for as long as the team is subscribed).
CREATE TABLE IF NOT EXISTS promo_redemptions (
    code_id      UUID        NOT NULL REFERENCES promo_codes(code_id),
    team_id      UUID        NOT NULL REFERENCES teams(team_id),
    redeemed_by  UUID,
    redeemed_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    until        TIMESTAMPTZ,
    PRIMARY KEY (code_id, team_id)
);

CREATE INDEX IF NOT EXISTS idx_promo_redemptions_team ON promo_redemptions (team_id);
CREATE INDEX IF NOT EXISTS idx_audit_log_created ON audit_log (created_at);
