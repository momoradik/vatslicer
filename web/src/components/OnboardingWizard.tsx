import { useState } from 'react'

interface Props { onComplete: () => void }

const steps = [
  { title: 'Welcome to VATSlicer', body: 'Industrial-grade resin slicer with physics-driven support generation.' },
  { title: 'Import a Model', body: 'Drag and drop STL, OBJ, or 3MF files into the Slicer view. Multiple models can be arranged automatically.' },
  { title: 'Configure Printer', body: 'Set up your printer in Printer Setup: resolution, build volume, exposure times, and lift parameters.' },
  { title: 'Generate Supports', body: 'Choose support type, reinforcement, and density in the visual picker. Click Generate to create physics-sized supports.' },
  { title: 'Analyze & Export', body: 'Use Analyze mode to check overhangs. Slice and export to CTB, PWMX, SL1, or 5 other formats. Press ? for keyboard shortcuts.' },
]

export default function OnboardingWizard({ onComplete }: Props) {
  const [step, setStep] = useState(0)
  const current = steps[step]
  const isLast = step === steps.length - 1

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-sm">
      <div className="bg-gray-900 border border-gray-700 rounded-2xl p-8 w-[420px] shadow-2xl">
        {/* Progress dots */}
        <div className="flex justify-center gap-2 mb-6">
          {steps.map((_, i) => (
            <span key={i} className={`w-2 h-2 rounded-full transition-all ${i === step ? 'bg-teal-400 scale-125' : i < step ? 'bg-teal-600' : 'bg-gray-700'}`} />
          ))}
        </div>

        <h2 className="text-lg font-bold text-white text-center mb-3">{current.title}</h2>
        <p className="text-sm text-gray-400 text-center leading-relaxed mb-8">{current.body}</p>

        <div className="flex justify-between">
          <button
            onClick={() => step > 0 ? setStep(step - 1) : onComplete()}
            className="px-4 py-2 text-xs text-gray-500 hover:text-gray-300 transition"
          >
            {step > 0 ? 'Back' : 'Skip'}
          </button>
          <button
            onClick={() => isLast ? onComplete() : setStep(step + 1)}
            className="px-6 py-2 text-xs font-medium bg-teal-500 text-white rounded-lg hover:bg-teal-400 transition shadow-sm"
          >
            {isLast ? 'Get Started' : 'Next'}
          </button>
        </div>
      </div>
    </div>
  )
}
