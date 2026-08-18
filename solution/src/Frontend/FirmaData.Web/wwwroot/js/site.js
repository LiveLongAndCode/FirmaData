// Loading state on submit (plan section 15): opt in per-form with data-loading-form. Skipped
// entirely if unobtrusive client-side validation already blocked the submit (event.defaultPrevented),
// so an invalid form doesn't flash a "Søger…" state it's never going to act on.
document.addEventListener("submit", function (event) {
    if (event.defaultPrevented) {
        return;
    }

    var form = event.target.closest("[data-loading-form]");
    if (!form) {
        return;
    }

    var submitButton = form.querySelector('button[type="submit"], input[type="submit"]');
    if (!submitButton || submitButton.disabled) {
        return;
    }

    submitButton.disabled = true;
    submitButton.textContent = form.dataset.loadingText || submitButton.textContent;
    form.classList.add("is-loading");
});
