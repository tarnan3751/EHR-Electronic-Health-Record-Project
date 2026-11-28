// The microphone on Linux. WebKitGTK gives a page no media capture unless the app turns it on in the web view's
// settings and answers each permission request; macOS and Windows ask the person themselves. So on Linux the app
// turns capture on for its window and allows one thing: its own page asking for the microphone alone. Anything else
// (a camera, a location, a notification, a request from any other address) is refused.

/// What a media request asks for.
#[cfg(any(target_os = "linux", test))]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Asked {
    pub audio: bool,
    pub video: bool,
}

/// Whether a permission request is granted: `asked` is `None` for a request that isn't for media capture, and
/// `page` is the address of the page that asks.
#[cfg(any(target_os = "linux", test))]
pub fn allowed(asked: Option<Asked>, page: Option<&str>) -> bool {
    let microphone_alone = asked
        == Some(Asked {
            audio: true,
            video: false,
        });
    microphone_alone && page.is_some_and(own_page)
}

/// The app's own page: what the bundle serves, at `tauri://localhost` on Linux.
#[cfg(any(target_os = "linux", test))]
fn own_page(address: &str) -> bool {
    address == "tauri://localhost" || address.starts_with("tauri://localhost/")
}

/// Turns media capture on for the window's web view and answers its permission requests.
#[cfg(target_os = "linux")]
pub fn enable(window: &tauri::WebviewWindow) -> tauri::Result<()> {
    use webkit2gtk::glib::prelude::Cast;
    use webkit2gtk::{
        PermissionRequestExt, SettingsExt, UserMediaPermissionRequest,
        UserMediaPermissionRequestExt, WebViewExt,
    };

    window.with_webview(|webview| {
        let view = webview.inner();
        if let Some(settings) = WebViewExt::settings(&view) {
            settings.set_enable_media_stream(true);
        }
        view.connect_permission_request(|view, request| {
            let asked = request
                .downcast_ref::<UserMediaPermissionRequest>()
                .map(|media| Asked {
                    audio: media.is_for_audio_device(),
                    video: media.is_for_video_device(),
                });
            if allowed(asked, view.uri().as_deref()) {
                request.allow();
            } else {
                request.deny();
            }
            true
        });
    })
}

#[cfg(test)]
mod tests {
    use super::{Asked, allowed};

    const MICROPHONE: Option<Asked> = Some(Asked {
        audio: true,
        video: false,
    });

    #[test]
    fn the_apps_own_page_gets_the_microphone() {
        assert!(allowed(MICROPHONE, Some("tauri://localhost")));
        assert!(allowed(MICROPHONE, Some("tauri://localhost/index.html")));
    }

    #[test]
    fn a_camera_is_refused_with_or_without_the_microphone() {
        assert!(!allowed(
            Some(Asked {
                audio: false,
                video: true
            }),
            Some("tauri://localhost/")
        ));
        assert!(!allowed(
            Some(Asked {
                audio: true,
                video: true
            }),
            Some("tauri://localhost/")
        ));
    }

    #[test]
    fn a_request_that_is_not_for_media_is_refused() {
        assert!(!allowed(None, Some("tauri://localhost/")));
        assert!(!allowed(
            Some(Asked {
                audio: false,
                video: false
            }),
            Some("tauri://localhost/")
        ));
    }

    #[test]
    fn another_address_is_refused() {
        for page in [
            "https://example.com/",
            "tauri://localhost.example.com/",
            "http://localhost/",
            "about:blank",
        ] {
            assert!(!allowed(MICROPHONE, Some(page)), "{page}");
        }
        assert!(!allowed(MICROPHONE, None));
    }
}
