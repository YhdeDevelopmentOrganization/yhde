# YHDE (Your Hosted Development Environment)

Self-hosted real-time collaborative development platform for Godot.

# Vision

YHDE allows multiple developers to work on the same Godot project simultaneously with real-time editing, operation synchronization, asset synchronization, versioning, rollback, auditing, project management, and extensibility.

YHDE should behave more like a multiplayer engine than a file-sharing system.

Everything should be designed around immutable operations.

Scenes are generated state and are never synchronized directly.

# Core Principles

- Everything is an immutable operation.
- Server-authoritative architecture.
- Never trust clients.
- Never synchronize scene files.
- Send deltas only.
- Operations must be replayable.
- Operations must be undoable.
- Operations must support branching.
- Operations must support rollback.
- Everything should be extensible.
- Security is mandatory.
- Reliability is mandatory.

# Technology Stack

## Client

Godot 4.7.x

GDExtension C++

Subsystems:

- Networking
- Operation queue
- Local cache
- Asset manager
- Presence manager
- Authority manager
- Snapshot manager

## Server

C#

Subsystems:

- WebSocket gateway
- Operation processor
- Snapshot service
- Asset service
- Versioning service
- Permission service
- Authentication service
- Presence service

## Database

PostgreSQL

## Asset Storage

Filesystem

## Transport

Secure WebSockets (WSS)

# Operation System

Every edit is an operation.

Examples:

- CreateNode
- DeleteNode
- MoveNode
- RenameNode
- ChangeProperty
- AddResource
- RemoveResource

The server stores operations, not scene files.

Snapshots exist to prevent replaying huge logs.

Operations are immutable.

Operations must support:

- Replay
- Undo
- Branching
- Rollback
- Auditing
- Conflict resolution

# Real-Time Editing

Synchronize:

- Nodes
- Properties
- Resources
- Transforms
- Selections
- Cursors

Rules:

- Delta only
- No full scenes
- Low latency
- Efficient bandwidth

# Authority

Roles:

Owner
Admin
Developer
Artist
QA
Viewer

Lock Types:

Unlocked
Soft Lock
Hard Lock

Recommendations:

Transforms -> Soft

Properties -> Soft

Hierarchy -> Hard

Resource deletion -> Hard

# Versioning

Layer 1

Git

Layer 2

Realtime operation history

Features:

- Branches
- Tags
- Rollbacks
- Snapshots
- User history
- Scene history

# Asset System

Synchronize:

- Textures
- Models
- Audio
- Materials
- Shaders
- Scripts
- Scenes

Assets contain:

- UUID
- Hash
- Version

Rules:

- Delta uploads
- Deduplication
- No duplicate storage

# Conflict Resolution

Automatic:

Transforms

Latest wins

Properties

Latest wins

Hierarchy

Authority wins

Manual:

Destructive operations

All conflicts logged.

# Presence

Show:

- Online users
- Scene
- Tool
- Selected nodes
- Cursor position

Optional:

- Voice
- Comments
- Mentions

# Undo

Server authoritative.

Undo(operation_id)

Server validates and generates reverse operations.

No local undo.

# Plugin System

Allow:

- Custom operations
- Asset types
- Authority rules
- Synchronization handlers
- Inspectors

# Reliability

Must survive:

- Crashes
- Disconnects
- Power failures

Requirements:

- Replay queues
- Recovery
- Automatic reconnect
- Transactions
- Backups

No operation should ever be lost.

# Security

Zero trust.

Requirements:

- TLS
- JWT
- Refresh tokens
- Input validation
- Rate limiting
- Audit logs
- Integrity checks
- Binary signing
- Anti-tamper detection

C++ is used for performance, not security.

No software is impossible to crack.

Security should rely on architecture.

# Scalability

Target VPS:

2 vCPU
4GB RAM
40GB SSD
20TB traffic

Targets:

5-15 users easy

20-40 users ideal

50+ users with optimization

Bottlenecks:

CPU and database writes.

# Authentication

Implemented last.

Support:

- Email/password
- Google
- GitHub
- Discord
- Microsoft

Via website OAuth.

Support:

- Sessions
- Refresh tokens
- Remember device
- MFA
- Account linking

# Future Features

- Offline editing
- Delayed synchronization
- Session replay
- Scene diff viewer
- Asset diff viewer
- Collaborative debugging
- Task system
- Issue tracking
- AI merge assistance
- Team analytics
- Cloud deployment

# Guiding Principle

Everything is an immutable operation.

If the operation log is correct, versioning, rollback, replay, snapshots, conflict resolution, and synchronization become natural consequences instead of separate systems.