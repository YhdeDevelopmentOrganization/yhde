# ADR 0016: People Belong to Projects, Not Teams

- Status: Built
- Date: 2026-09-27
- See also: [teams.md](../teams.md), [onboarding.md](../onboarding.md),
  [authority.md](../authority.md), [admin.md](../admin.md),
  [ADR 0011](0011-projects-and-invite-codes.md), [ADR 0015](0015-accounts-and-sign-in.md)

## Situation

After accounts came (ADR 0015), people were grouped in teams: one owner with a
plan, members, and every account in exactly one team. In the beta that broke
down:

- Someone working with two groups could not be in both. Accepting an
  invitation had to throw them out of their old team.
- Test accounts and anyone already in a team never saw invitations, which
  only showed on the "make your team" page.
- Seats were counted per team, so one busy project used up the room of all
  the others, and invite links, email invitations and members competed for
  the same seats in ways nobody could follow.
- Nothing stopped a team's files from growing past its storage: the limit
  was only checked on zip imports.

## Decision

1. People belong to projects. An owner makes projects; everyone else is in a
   project because its owner invited them (`project_members`, access `edit`
   or `view`). Anyone can be in any number of projects.
2. Owning projects needs an access code during the beta; being invited never
   does. The owner's plan, storage, promo codes and free seats stay one row
   in `teams`, which now means "the owner's plan", not a group of people.
3. Beta tester limits: 3 projects and 2 GB for all of them, whichever comes
   first (archived projects count); each project has its owner and 3 invited
   people (members plus open invitations); each extra seat, paid, from a code
   or free from staff, adds one person to every project of that owner.
4. Only a project's owner decides who is in it, their access, its view
   links, and what happens to it. Members can leave. The owner can hand the
   project to a member who can own projects and has room for it; the old
   owner stays in as a member who edits.
5. An invitation is to one project and lasts 14 days. It is accepted on the
   dashboard (signed in with the confirmed address) or from the email's link,
   whose 256-bit secret is kept only as a hash and proves the email arrived.
   Resending gives a new link and 14 more days.
6. View links let up to 5 people per project watch it in Godot without an
   account. The invite codes they make are read-only (the gateway refuses
   their changes); codes made on the admin page still edit. A link holds its
   places until it is turned off, which also turns off its codes.
7. The server enforces storage as files arrive: a new file that would take
   the owner over their storage is refused before it is logged, with a
   reason the editor shows. The dashboard and the Godot panel warn from 80 %.
8. Migration 014 moves every team member into each team project they could
   open, with the same access, and turns open team invitations into
   invitations to each active project. Nobody is removed, even where that is
   more people than a project allows now.

## Why

- Projects are what people actually share. Tying membership to them removes
  the one-team rule without inventing a second grouping above projects.
- Keeping the `teams` row as the owner's plan kept promo codes, audit
  entries and billing fields as they were, and made the migration a data
  move rather than a rewrite.
- Counting people per project makes the limit match what an owner sees on
  one project's page, and makes every seat freeable in one place.
- Read-only view links separate "watching" from "working", so a playtester
  link can't change a game by accident and doesn't take an editor's place.
- Refusing a file before it is logged keeps the operation log the truth
  (ADR 0001): nothing enters the log that the owner hasn't room for, and the
  editor already handles a refused file change (it keeps the file locally).

## Consequences

- Gains: one account can work with any number of people; test accounts and
  people in other projects see their invitations; limits are visible where
  they apply.
- Gains: members leave on their own, owners hand projects over, and staff
  can put an existing account straight into a project.
- Cost: there is no group to invite people to all projects at once; an owner
  with three projects invites into each.
- Cost: the storage check counts a changed file's full new size only once the
  owner is already over (the old size isn't known at that point), so an owner
  can end slightly over the limit by changing files.
- Cost: Godot add-on 0.4 shows an empty team box and its team invitations
  are refused with a pointer to the website, until the next add-on release.
- Follow-up: the paid plans (from 1.0) reuse the same model with seats per
  project; their prices and wording need a look before 1.0.

## Rejected

### Keep teams, allow many per account
One account in several teams, with a team switcher. It keeps a grouping
nobody asked for, makes the dashboard and the Godot sign-in pick a team
first, and still counts seats across unrelated projects.

### Seats per owner across all projects
One pool of people per owner. Simpler to count, but a person in two of the
owner's projects would take two seats or none depending on the rule, and a
busy project would starve the others, which is the problem this replaces.

### Storage checked only on the website
Refusing uploads only on zip imports and new projects. Files added in Godot
are most of a project's growth, so the limit would not hold.
