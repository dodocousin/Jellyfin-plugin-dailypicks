// Daily Picks: draws a "Daily Picks" row on the web client home screen, right after "My Media".
(function () {
    'use strict';

    const SECTION_CLASS = 'dailyPicksSection';
    let scheduled = false;
    let libraryId = null;

    function hideLibraryLatestRow(container) {
        // Native TV apps need the Daily Picks library's "Recently added" row; here our own row already shows the picks.
        if (!libraryId) {
            return;
        }

        for (const link of container.querySelectorAll('a.sectionTitleTextButton[href*="' + libraryId + '"]')) {
            const row = link.closest('.verticalSection');
            if (row && !row.classList.contains(SECTION_CLASS)) {
                row.classList.add('hide');
            }
        }
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text == null ? '' : String(text);
        return div.innerHTML;
    }

    function findHomeContainer() {
        const containers = document.querySelectorAll('.homeSectionsContainer');
        for (const container of containers) {
            // Hidden pages stay in the DOM; only use the one on screen.
            if (container.offsetParent !== null) {
                return container;
            }
        }

        return null;
    }

    function findMyMediaSection(container) {
        // "My Media" is the home section holding the library tiles or library buttons.
        for (const child of container.children) {
            if (child.querySelector('.homeLibraryButtonContainer, .card[data-type="CollectionFolder"], .card[data-type="UserView"]')) {
                return child;
            }
        }

        return null;
    }

    function placeSection(container, section) {
        const myMedia = findMyMediaSection(container);
        if (myMedia) {
            if (myMedia.nextElementSibling !== section) {
                myMedia.after(section);
            }
        } else if (container.firstElementChild !== section) {
            container.prepend(section);
        }
    }

    function cardHtml(item, reason, apiClient) {
        const imageTag = item.ImageTags && item.ImageTags.Primary;
        const imageUrl = imageTag
            ? apiClient.getScaledImageUrl(item.Id, { type: 'Primary', maxHeight: 450, tag: imageTag })
            : '';
        const href = '#/details?id=' + item.Id + '&serverId=' + apiClient.serverId();
        // "Because you watched ..." when the server gives a reason, otherwise the year.
        const subtitle = reason || (item.Type === 'Series' ? 'Show' : (item.ProductionYear || ''));

        return '<div class="card overflowPortraitCard card-hoverable" data-id="' + item.Id + '">'
            + '<div class="cardBox cardBox-bottompadded">'
            + '<div class="cardScalable">'
            + '<div class="cardPadder cardPadder-overflowPortrait"></div>'
            + '<a class="cardImageContainer coveredImage cardContent" href="' + href + '"'
            + ' title="' + escapeHtml(item.Name) + '"'
            + (imageUrl ? ' style="background-image:url(\'' + imageUrl + '\')"' : '')
            + '></a>'
            + '</div>'
            + '<div class="cardText cardTextCentered cardText-first"><bdi>' + escapeHtml(item.Name) + '</bdi></div>'
            + '<div class="cardText cardTextCentered cardText-secondary" title="' + escapeHtml(subtitle) + '"><bdi>' + escapeHtml(subtitle) + '</bdi></div>'
            + '</div></div>';
    }

    async function fillSection(section, apiClient) {
        const today = await apiClient.getJSON(apiClient.getUrl('DailyPicks/Today'));
        libraryId = today.LibraryId;
        hideLibraryLatestRow(section.parentElement || document);
        if (!today.Enabled || !today.Items.length) {
            section.remove();
            return;
        }

        const ids = today.Items.map(pick => pick.Id);
        const reasons = new Map(today.Items.map(pick => [pick.Id, pick.Reason]));
        const result = await apiClient.getItems(apiClient.getCurrentUserId(), {
            Ids: ids.join(','),
            Fields: 'PrimaryImageAspectRatio',
            EnableImageTypes: 'Primary',
            ImageTypeLimit: 1
        });

        // The server doesn't keep the order of Ids, so put it back (best picks first).
        const order = new Map(ids.map((id, index) => [id, index]));
        const items = (result.Items || []).sort((a, b) => order.get(a.Id) - order.get(b.Id));
        if (!items.length) {
            section.remove();
            return;
        }

        section.querySelector('.sectionTitle').textContent = today.Title;
        section.querySelector('.dailyPicksItems').innerHTML = items.map(item => cardHtml(item, reasons.get(item.Id), apiClient)).join('');
        section.classList.remove('hide');
    }

    function render() {
        scheduled = false;
        const apiClient = window.ApiClient;
        const container = findHomeContainer();
        if (!apiClient || !container) {
            return;
        }

        hideLibraryLatestRow(container);

        // Home sections load one by one, so keep the row after "My Media" as they appear.
        const existing = container.querySelector(':scope > .' + SECTION_CLASS);
        if (existing) {
            placeSection(container, existing);
            return;
        }

        // Add the section right away (hidden) so later DOM changes don't insert it twice.
        const section = document.createElement('div');
        section.className = 'verticalSection hide ' + SECTION_CLASS;
        section.innerHTML = '<div class="sectionTitleContainer sectionTitleContainer-cards padded-left">'
            + '<h2 class="sectionTitle sectionTitle-cards"></h2></div>'
            + '<div class="padded-left padded-right" style="overflow-x:auto;overflow-y:hidden;">'
            // Not "itemsContainer": the home screen calls pause()/resume() on those, which only its own elements have.
            + '<div class="dailyPicksItems focuscontainer-x" style="display:flex;flex-wrap:nowrap;"></div></div>';
        placeSection(container, section);

        fillSection(section, apiClient).catch(err => {
            console.warn('Daily Picks: could not load row', err);
            section.remove();
        });
    }

    // The home screen is rebuilt every time it is shown, so watch for it.
    new MutationObserver(() => {
        if (!scheduled) {
            scheduled = true;
            setTimeout(render, 250);
        }
    }).observe(document.body, { childList: true, subtree: true });
})();
