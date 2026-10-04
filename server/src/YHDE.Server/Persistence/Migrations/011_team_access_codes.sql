-- 011: making a team needs an access code (teams.md, admin.md)

-- Until payments start, only people given a code may make a team, so
-- strangers can't fill the server. A code with unlocks_teams set works on the
-- "Make your team" page; it may also give a benefit, or nothing else
-- (kind 'nothing'). Invited people join without a code.
ALTER TABLE promo_codes ADD COLUMN IF NOT EXISTS unlocks_teams BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE promo_codes DROP CONSTRAINT IF EXISTS promo_codes_kind_check;
ALTER TABLE promo_codes ADD CONSTRAINT promo_codes_kind_check
    CHECK (kind IN ('nothing', 'free_months', 'percent_off', 'amount_off', 'extra_seats', 'extra_storage'));
