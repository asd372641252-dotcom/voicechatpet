from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass, field
from typing import Callable

from .client import DEFAULT_ENDPOINT, PetPerformResult, PetPerformTool
from .protocol import ALLOWED_PHASES, PetPerformCommand, PetPerformCommandError


LOGGER = logging.getLogger(__name__)
ReminderCallback = Callable[["PetPerformReminder"], None]


@dataclass(frozen=True)
class PetPerformReminder:
    """A structured reminder that the Agent missed or failed a pet performance."""

    task_id: str
    phase: str
    reason: str
    expected_text: str = ""
    detail: str = ""
    created_at: float = field(default_factory=time.time)

    def to_agent_message(self) -> str:
        if self.reason == "missing_pet_perform":
            return (
                f"Reminder: phase={self.phase!r} for task_id={self.task_id!r} expected a pet_perform call, "
                "but none was observed. Call pet_perform now if this stage should be visible to the user."
            )
        if self.reason == "pet_perform_failed":
            return (
                f"Reminder: pet_perform was attempted for phase={self.phase!r} task_id={self.task_id!r}, "
                f"but it did not reach the pet. Detail: {self.detail}"
            )
        return f"Reminder: pet performance issue for task_id={self.task_id!r} phase={self.phase!r}: {self.detail}"

    def to_dict(self) -> dict[str, object]:
        return {
            "task_id": self.task_id,
            "phase": self.phase,
            "reason": self.reason,
            "expected_text": self.expected_text,
            "detail": self.detail,
            "created_at": self.created_at,
            "agent_message": self.to_agent_message(),
        }


@dataclass
class _ExpectedPetCall:
    sequence: int
    task_id: str
    phase: str
    expected_text: str
    due_at: float
    created_at: float
    required: bool = True
    satisfied: bool = False
    reminded: bool = False


class AgentPetReminderGuard:
    """Detects missed pet_perform calls and reminds the Agent.

    This class does not auto-broadcast. The Agent or runner emits lifecycle
    expectations through expect_phase(), then records real pet_perform attempts
    through a MonitoredPetPerformTool. If a required phase has no matching
    pet_perform call before its grace period expires, a reminder is produced.
    """

    def __init__(
        self,
        *,
        task_id: str,
        grace_sec: float = 1.5,
        reminder_callback: ReminderCallback | None = None,
        logger: logging.Logger | None = None,
    ) -> None:
        self.task_id = task_id
        self.grace_sec = max(0.0, grace_sec)
        self.reminder_callback = reminder_callback
        self.logger = logger or LOGGER
        self._sequence = 0
        self._expected: list[_ExpectedPetCall] = []
        self._reminders: list[PetPerformReminder] = []
        self._lock = threading.Lock()

    def expect_phase(
        self,
        phase: str,
        expected_text: str = "",
        *,
        task_id: str | None = None,
        grace_sec: float | None = None,
        required: bool = True,
    ) -> int:
        phase = _normalize_phase(phase)
        now = time.monotonic()
        with self._lock:
            self._sequence += 1
            item = _ExpectedPetCall(
                sequence=self._sequence,
                task_id=task_id or self.task_id,
                phase=phase,
                expected_text=expected_text,
                due_at=now + (self.grace_sec if grace_sec is None else max(0.0, grace_sec)),
                created_at=now,
                required=required,
            )
            self._expected.append(item)
            return item.sequence

    def record_pet_call(self, command: PetPerformCommand, result: PetPerformResult) -> None:
        with self._lock:
            self._satisfy_matching_expectation(command.task_id, command.phase)

        if result.accepted or result.sent or result.queued or result.throttled:
            return

        self._emit_reminder(
            PetPerformReminder(
                task_id=command.task_id,
                phase=command.phase,
                reason="pet_perform_failed",
                expected_text=command.text,
                detail=result.error or f"status_code={result.status_code}",
            )
        )

    def check(self) -> list[PetPerformReminder]:
        now = time.monotonic()
        due: list[PetPerformReminder] = []
        with self._lock:
            for item in self._expected:
                if item.satisfied or item.reminded or not item.required or now < item.due_at:
                    continue
                item.reminded = True
                due.append(
                    PetPerformReminder(
                        task_id=item.task_id,
                        phase=item.phase,
                        reason="missing_pet_perform",
                        expected_text=item.expected_text,
                    )
                )

        for reminder in due:
            self._emit_reminder(reminder)
        return due

    def consume_reminders(self) -> list[PetPerformReminder]:
        with self._lock:
            reminders = list(self._reminders)
            self._reminders.clear()
            return reminders

    def pending_count(self) -> int:
        with self._lock:
            return sum(1 for item in self._expected if not item.satisfied and not item.reminded)

    def _satisfy_matching_expectation(self, task_id: str, phase: str) -> None:
        for item in reversed(self._expected):
            if item.task_id == task_id and item.phase == phase and not item.satisfied and not item.reminded:
                item.satisfied = True
                return

    def _emit_reminder(self, reminder: PetPerformReminder) -> None:
        with self._lock:
            self._reminders.append(reminder)
        self.logger.warning(reminder.to_agent_message())
        if self.reminder_callback is not None:
            self.reminder_callback(reminder)


class MonitoredPetPerformTool(PetPerformTool):
    """PetPerformTool variant that reports attempts to an AgentPetReminderGuard."""

    def __init__(
        self,
        guard: AgentPetReminderGuard,
        *,
        endpoint: str = DEFAULT_ENDPOINT,
        api_key: str = "",
        timeout_sec: float = 1.2,
        throttle_sec: float = 8.0,
        logger: logging.Logger | None = None,
    ) -> None:
        super().__init__(
            endpoint=endpoint,
            api_key=api_key,
            timeout_sec=timeout_sec,
            throttle_sec=throttle_sec,
            logger=logger,
        )
        self.guard = guard

    def pet_perform(self, command: PetPerformCommand | dict[str, object]) -> PetPerformResult:
        try:
            parsed = command if isinstance(command, PetPerformCommand) else PetPerformCommand.from_mapping(command)
        except PetPerformCommandError:
            return super().pet_perform(command)

        result = super().pet_perform(parsed)
        self.guard.record_pet_call(parsed, result)
        return result


class AgentPetSession:
    """Convenience object for task-scoped pet performance monitoring."""

    def __init__(
        self,
        task_id: str,
        *,
        endpoint: str = DEFAULT_ENDPOINT,
        api_key: str = "",
        timeout_sec: float = 1.2,
        throttle_sec: float = 8.0,
        reminder_grace_sec: float = 1.5,
        reminder_callback: ReminderCallback | None = None,
        logger: logging.Logger | None = None,
    ) -> None:
        self.task_id = task_id
        self.guard = AgentPetReminderGuard(
            task_id=task_id,
            grace_sec=reminder_grace_sec,
            reminder_callback=reminder_callback,
            logger=logger,
        )
        self.pet = MonitoredPetPerformTool(
            self.guard,
            endpoint=endpoint,
            api_key=api_key,
            timeout_sec=timeout_sec,
            throttle_sec=throttle_sec,
            logger=logger,
        )

    def expect(self, phase: str, expected_text: str = "", *, grace_sec: float | None = None) -> int:
        return self.guard.expect_phase(phase, expected_text, task_id=self.task_id, grace_sec=grace_sec)

    def check_reminders(self) -> list[PetPerformReminder]:
        return self.guard.check()

    def consume_reminders(self) -> list[PetPerformReminder]:
        return self.guard.consume_reminders()


def _normalize_phase(phase: str) -> str:
    value = str(phase).strip().lower()
    if value not in ALLOWED_PHASES:
        raise ValueError(f"Invalid pet phase {value!r}; allowed values are {sorted(ALLOWED_PHASES)}")
    return value
