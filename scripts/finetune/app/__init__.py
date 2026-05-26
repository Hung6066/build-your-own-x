"""Hope.Agent Training Service — Clean Architecture layout.

Layers (Dependency Rule: outer depends on inner only):

    domain         ← pure entities, value objects, exceptions
        ↑
    application    ← ports (Protocols), use cases
        ↑
    infrastructure ← SQLite, HF, HTTP, logging — implements ports
        ↑
    interfaces     ← FastAPI, APScheduler — entry adapters
"""

__version__ = "15.0.0"
