"""Agent-side bridge for controlling the desktop pet as a performance tool."""

from .client import DEFAULT_ENDPOINT, PetPerformResult, PetPerformTool, pet_perform
from .companion import AgentCompanionMode, CompanionEvent
from .companion_daemon import AgentCompanionDaemon, CompanionDaemonSettings
from .protocol import PetPerformCommand, PetPerformCommandError
from .session import AgentPetReminderGuard, AgentPetSession, MonitoredPetPerformTool, PetPerformReminder

__all__ = [
    "AgentCompanionDaemon",
    "AgentCompanionMode",
    "AgentPetReminderGuard",
    "AgentPetSession",
    "CompanionDaemonSettings",
    "CompanionEvent",
    "DEFAULT_ENDPOINT",
    "MonitoredPetPerformTool",
    "PetPerformCommand",
    "PetPerformCommandError",
    "PetPerformReminder",
    "PetPerformResult",
    "PetPerformTool",
    "pet_perform",
]
