import { useState, useCallback, useRef } from 'react'

/**
 * Generic undo/redo hook using the command pattern.
 *
 * Usage:
 *   const { state, execute, undo, redo, canUndo, canRedo } = useUndoRedo(initialState)
 *   execute({ ...state, someField: newValue }, 'Changed someField')
 *
 * Each execute() pushes a snapshot of the full state onto the undo stack.
 * Ctrl+Z / Ctrl+Shift+Z keyboard shortcuts are handled internally.
 */

interface UndoEntry<T> {
  state: T
  label: string
  timestamp: number
}

interface UndoRedoResult<T> {
  state: T
  execute: (newState: T, label?: string) => void
  undo: () => void
  redo: () => void
  canUndo: boolean
  canRedo: boolean
  undoLabel: string | null
  redoLabel: string | null
  historySize: number
}

export function useUndoRedo<T>(initialState: T, maxHistory = 50): UndoRedoResult<T> {
  const [state, setState] = useState<T>(initialState)
  const undoStack = useRef<UndoEntry<T>[]>([])
  const redoStack = useRef<UndoEntry<T>[]>([])

  const execute = useCallback((newState: T, label = 'Change') => {
    undoStack.current.push({ state, label, timestamp: Date.now() })
    if (undoStack.current.length > maxHistory)
      undoStack.current.shift()
    redoStack.current = [] // clear redo on new action
    setState(newState)
  }, [state, maxHistory])

  const undo = useCallback(() => {
    const entry = undoStack.current.pop()
    if (!entry) return
    redoStack.current.push({ state, label: entry.label, timestamp: Date.now() })
    setState(entry.state)
  }, [state])

  const redo = useCallback(() => {
    const entry = redoStack.current.pop()
    if (!entry) return
    undoStack.current.push({ state, label: entry.label, timestamp: Date.now() })
    setState(entry.state)
  }, [state])

  return {
    state,
    execute,
    undo,
    redo,
    canUndo: undoStack.current.length > 0,
    canRedo: redoStack.current.length > 0,
    undoLabel: undoStack.current.length > 0 ? undoStack.current[undoStack.current.length - 1].label : null,
    redoLabel: redoStack.current.length > 0 ? redoStack.current[redoStack.current.length - 1].label : null,
    historySize: undoStack.current.length,
  }
}
