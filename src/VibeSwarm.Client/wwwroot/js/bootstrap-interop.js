// Blazor's bridge to Bootstrap 6. Bootstrap ships as ES modules with no `window.bootstrap`
// global, so components that drive it from .NET import this module through IJSRuntime.
// It imports the same bundle URL as the <script type="module"> tag in index.html, so the
// page has a single Bootstrap instance and its data API (menus, dismiss buttons) keeps
// working on markup Blazor renders.
import { Dialog } from '../lib/bootstrap/js/bootstrap.bundle.min.js';

const toastLayerSelector = '[data-vs-toast-layer]';

// Opens a <dialog class="dialog"> as a modal. When the person dismisses it themselves
// (Escape, the backdrop), .NET hears about it through HandleDialogDismissed.
export async function showDialog(element, dotNetRef, options) {
	if (!element || element.open) {
		return;
	}

	const dialog = Dialog.getOrCreateInstance(element, {
		backdrop: options?.closeOnBackdrop === false ? 'static' : true,
		keyboard: options?.closeOnEscape !== false
	});

	// A text selection dragged from inside the dialog out onto the backdrop ends in a
	// click on the <dialog> itself, which Bootstrap reads as a backdrop click. Only a
	// press that starts on the backdrop may close it.
	let pressStartedInside = false;
	element.addEventListener('pointerdown', event => {
		pressStartedInside = event.target !== element;
	}, true);
	element.addEventListener('click', event => {
		if (event.target === element && pressStartedInside) {
			event.stopImmediatePropagation();
		}
		pressStartedInside = false;
	}, true);

	element.addEventListener('hidden.bs.dialog', () => {
		if (element.dataset.vsClosing === 'true') {
			return;
		}

		dialog.dispose();
		dotNetRef?.invokeMethodAsync('HandleDialogDismissed');
	}, { once: true });

	// Blazor has only just inserted the element: settle its closed styles first, so
	// opening it transitions instead of popping in.
	void element.offsetWidth;
	await dialog.show();

	const toastLayer = document.querySelector(toastLayerSelector);
	if (toastLayer?.matches(':popover-open')) {
		raiseToastLayer();
	}
}

// Closes a dialog .NET opened, waiting for the exit transition so the element can be
// removed afterwards.
export async function hideDialog(element) {
	const dialog = element ? Dialog.getInstance(element) : null;
	if (!dialog) {
		return;
	}

	element.dataset.vsClosing = 'true';
	await dialog.hide();
	dialog.dispose();
}

// Tears a dialog down at once, for a component removed while its dialog is still open.
export function disposeDialog(element) {
	if (!element) {
		return;
	}

	element.dataset.vsClosing = 'true';
	Dialog.getInstance(element)?.dispose();
}

// Toasts live in a manual popover, so they share the browser's top layer with modal
// dialogs. The top layer stacks in the order things open, so the toast layer is shown
// again whenever a toast arrives or a dialog opens over it, keeping toasts on top.
export function raiseToastLayer() {
	const layer = document.querySelector(toastLayerSelector);
	if (!layer || typeof layer.showPopover !== 'function') {
		return;
	}

	if (layer.matches(':popover-open')) {
		layer.hidePopover();
	}

	layer.showPopover();
}

export function hideToastLayer() {
	const layer = document.querySelector(toastLayerSelector);
	if (layer?.matches(':popover-open')) {
		layer.hidePopover();
	}
}
